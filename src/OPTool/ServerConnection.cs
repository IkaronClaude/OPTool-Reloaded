using System.Net.Sockets;
using FiestaLibReloaded.Config;
using FiestaLibReloaded.Networking;

namespace OPTool;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected
}

public class ServerConnection : IDisposable
{
    // MISC department opcodes (dept 2, << 10 = 0x0800)
    private const ushort OpS2SConnectionRdy = (2 << 10) | 1; // 0x0801
    private const ushort OpS2SConnectionReq = (2 << 10) | 2; // 0x0802
    private const ushort OpS2SConnectionAck = (2 << 10) | 3; // 0x0803
    private const ushort OpHeartbeatReq = (2 << 10) | 4;     // 0x0804
    private const ushort OpHeartbeatAck = (2 << 10) | 5;     // 0x0805

    private readonly ServerInfoEntry _endpoint;
    private readonly int _handshakeKey;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ILogger _logger;

    private TcpClient? _tcp;
    private FiestaConnection? _conn;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private Task? _receiveTask;
    private bool _disposed;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<ushort, TaskCompletionSource<FiestaPacket>> _pending = new();

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ServerInfoEntry Endpoint => _endpoint;

    public ServerConnection(ServerInfoEntry endpoint, int handshakeKey, TimeSpan heartbeatInterval, ILogger logger)
    {
        _endpoint = endpoint;
        _handshakeKey = handshakeKey;
        _heartbeatInterval = heartbeatInterval;
        _logger = logger;
    }

    public async Task ConnectAndHandshakeAsync(CancellationToken ct)
    {
        State = ConnectionState.Connecting;
        _logger.LogInformation("[{Name}] Connecting to {Ip}:{Port}...", _endpoint.Name, _endpoint.IpAddress, _endpoint.Port);

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_endpoint.IpAddress, _endpoint.Port, ct);
        _conn = new FiestaConnection(_tcp.GetStream());

        _logger.LogInformation("[{Name}] TCP connected, starting handshake", _endpoint.Name);
        State = ConnectionState.Handshaking;

        // Step 1: Read S2SCONNECTION_RDY (empty payload)
        var rdyPacket = await _conn.ReadPacketAsync(ct);
        if (rdyPacket.Opcode != OpS2SConnectionRdy)
        {
            throw new InvalidOperationException(
                $"Expected S2SCONNECTION_RDY (0x{OpS2SConnectionRdy:X4}), got 0x{rdyPacket.Opcode:X4}");
        }
        _logger.LogDebug("[{Name}] Received S2SCONNECTION_RDY", _endpoint.Name);

        // Step 2: Send S2SCONNECTION_REQ
        // Payload: server_from_id (byte), server_to_id (byte), key (int32 LE)
        var reqPayload = new byte[6];
        reqPayload[0] = (byte)FiestaServerType.OpTool; // 8
        reqPayload[1] = (byte)_endpoint.ServerType;
        BitConverter.TryWriteBytes(reqPayload.AsSpan(2), _handshakeKey);
        var reqPacket = new FiestaPacket(OpS2SConnectionReq, reqPayload);
        await _conn.WritePacketAsync(reqPacket, ct);
        _logger.LogDebug("[{Name}] Sent S2SCONNECTION_REQ (from={From}, to={To})",
            _endpoint.Name, (int)FiestaServerType.OpTool, _endpoint.ServerType);

        // Step 3: Read S2SCONNECTION_ACK
        var ackPacket = await _conn.ReadPacketAsync(ct);
        if (ackPacket.Opcode != OpS2SConnectionAck)
        {
            throw new InvalidOperationException(
                $"Expected S2SCONNECTION_ACK (0x{OpS2SConnectionAck:X4}), got 0x{ackPacket.Opcode:X4}");
        }

        // ACK payload: error (byte) at offset 0
        var error = ackPacket.Payload.Span[0];
        if (error != 0)
        {
            throw new InvalidOperationException($"S2SCONNECTION_ACK error code: {error}");
        }

        State = ConnectionState.Connected;
        _logger.LogInformation("[{Name}] Handshake complete - connected!", _endpoint.Name);

        // Start background loops
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_heartbeatInterval, ct);
                if (_conn == null || State != ConnectionState.Connected) break;

                var hbPacket = new FiestaPacket(OpHeartbeatReq, ReadOnlyMemory<byte>.Empty);
                await _conn.WritePacketAsync(hbPacket, ct);
                _logger.LogTrace("[{Name}] Sent heartbeat", _endpoint.Name);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Name}] Heartbeat failed", _endpoint.Name);
                State = ConnectionState.Disconnected;
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_conn == null || State != ConnectionState.Connected) break;

                var packet = await _conn.ReadPacketAsync(ct);

                if (packet.Opcode == OpHeartbeatAck)
                {
                    _logger.LogTrace("[{Name}] Heartbeat ACK received", _endpoint.Name);
                }
                else if (TryCompletePending(packet))
                {
                    _logger.LogDebug("[{Name}] Completed pending request for 0x{Opcode:X4}",
                        _endpoint.Name, packet.Opcode);
                }
                else
                {
                    _logger.LogDebug("[{Name}] Received opcode 0x{Opcode:X4} ({Len} bytes payload)",
                        _endpoint.Name, packet.Opcode, packet.Payload.Length);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (EndOfStreamException)
            {
                _logger.LogWarning("[{Name}] Server closed connection", _endpoint.Name);
                State = ConnectionState.Disconnected;
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Name}] Receive failed", _endpoint.Name);
                State = ConnectionState.Disconnected;
                break;
            }
        }
    }

    public async Task SendAsync(FiestaPacket packet, CancellationToken ct = default)
    {
        if (_conn == null || State != ConnectionState.Connected)
            throw new InvalidOperationException("Not connected");
        await _sendLock.WaitAsync(ct);
        try { await _conn.WritePacketAsync(packet, ct); }
        finally { _sendLock.Release(); }
    }

    public async Task<FiestaPacket> SendAndWaitAsync(FiestaPacket request, ushort expectedAckOpcode, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<FiestaPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
            _pending[expectedAckOpcode] = tcs;

        try
        {
            await SendAsync(request, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            return await tcs.Task;
        }
        finally
        {
            lock (_pending)
                _pending.Remove(expectedAckOpcode);
        }
    }

    private bool TryCompletePending(FiestaPacket packet)
    {
        TaskCompletionSource<FiestaPacket>? tcs;
        lock (_pending)
        {
            if (!_pending.TryGetValue(packet.Opcode, out tcs))
                return false;
        }
        return tcs.TrySetResult(packet);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        State = ConnectionState.Disconnected;
        _conn?.Dispose();
        _conn = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Disconnect();
            _cts?.Dispose();
        }
    }
}
