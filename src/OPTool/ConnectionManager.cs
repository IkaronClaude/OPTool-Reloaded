using FiestaLibReloaded.Config;

namespace OPTool;

public class ConnectionManager : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<ServerConnection> _connections = new();
    private readonly Dictionary<string, ConnectionState> _status = new();

    public IReadOnlyDictionary<string, ConnectionState> Status
    {
        get
        {
            lock (_status)
                return new Dictionary<string, ConnectionState>(_status);
        }
    }

    public ConnectionManager(IConfiguration config, ILogger<ConnectionManager> logger, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverInfoPath = _config["Fiesta:ServerInfoPath"];
        var handshakeKey = _config.GetValue("Fiesta:HandshakeKey", 0);
        var heartbeatSec = _config.GetValue("Fiesta:HeartbeatIntervalSeconds", 30);
        var heartbeatInterval = TimeSpan.FromSeconds(heartbeatSec);
        var reconnectDelay = TimeSpan.FromSeconds(5);

        if (string.IsNullOrEmpty(serverInfoPath) || !File.Exists(serverInfoPath))
        {
            _logger.LogError("ServerInfo.txt not found at: {Path}", serverInfoPath);
            return;
        }

        var endpoints = ServerInfoParser.GetOpToolEndpoints(serverInfoPath);
        _logger.LogInformation("Found {Count} OpTool endpoints in ServerInfo.txt", endpoints.Count);

        foreach (var ep in endpoints)
        {
            _logger.LogInformation("  {Name} -> {Ip}:{Port} (ServerType={Type})",
                ep.Name, ep.IpAddress, ep.Port, ep.ServerType);
        }

        if (endpoints.Count == 0)
        {
            _logger.LogWarning("No OpTool endpoints found (FromServerType=8). Nothing to connect to.");
            return;
        }

        // Launch a connection task per endpoint
        var tasks = endpoints.Select(ep => ManageConnectionAsync(ep, handshakeKey, heartbeatInterval, reconnectDelay, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task ManageConnectionAsync(
        ServerInfoEntry endpoint, int handshakeKey, TimeSpan heartbeatInterval,
        TimeSpan reconnectDelay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var conn = new ServerConnection(
                endpoint, handshakeKey, heartbeatInterval,
                _loggerFactory.CreateLogger($"OPTool.Conn.{endpoint.Name}"));

            lock (_connections)
                _connections.Add(conn);

            UpdateStatus(endpoint.Name, ConnectionState.Connecting);

            try
            {
                await conn.ConnectAndHandshakeAsync(ct);
                UpdateStatus(endpoint.Name, ConnectionState.Connected);

                // Wait until disconnected
                while (conn.State == ConnectionState.Connected && !ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Name}] Connection failed", endpoint.Name);
            }
            finally
            {
                conn.Disconnect();
                lock (_connections)
                    _connections.Remove(conn);
                conn.Dispose();
                UpdateStatus(endpoint.Name, ConnectionState.Disconnected);
            }

            if (ct.IsCancellationRequested) break;

            _logger.LogInformation("[{Name}] Reconnecting in {Seconds}s...", endpoint.Name, reconnectDelay.TotalSeconds);
            try { await Task.Delay(reconnectDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void UpdateStatus(string name, ConnectionState state)
    {
        lock (_status)
            _status[name] = state;
    }

    public override void Dispose()
    {
        lock (_connections)
        {
            foreach (var conn in _connections)
                conn.Dispose();
            _connections.Clear();
        }
        base.Dispose();
    }
}
