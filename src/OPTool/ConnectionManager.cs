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

        // Optional s2s-proxy / k8s addressing. The literal IP:port in
        // ServerInfo.txt is correct for a flat (non-proxied) deployment, but in
        // the fiesta-docker / k8s stack those peer rows are rewritten to
        // 127.0.0.1 (each pod's local proxy) and are meaningless from a separate
        // OpTool pod. EndpointHostOverrides remaps a row -- keyed by its
        // ServerInfo name, e.g. "PG_W00_WM" -- to an in-cluster DNS name
        // (worldmanager.fiesta.svc.cluster.local). S2sPortOffset optionally
        // shifts every port to the target pod's proxy inbound listener
        // (origPort + offset, matching S2S_INTERNAL_OFFSET) so the exe sees the
        // connection as 127.0.0.1 and its OpTool source-IP check passes. Both
        // default to no-op, so flat / non-s2s servers keep working unchanged.
        var hostOverrides = _config.GetSection("Fiesta:EndpointHostOverrides")
            .GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);
        var portOffset = _config.GetValue("Fiesta:S2sPortOffset", 0);
        if (hostOverrides.Count > 0 || portOffset != 0)
        {
            endpoints = endpoints
                .Select(ep => ep with
                {
                    IpAddress = hostOverrides.TryGetValue(ep.Name, out var host) ? host : ep.IpAddress,
                    Port = ep.Port + portOffset,
                })
                .ToList();
            _logger.LogInformation(
                "Applied addressing overrides: {Hosts} host override(s), port offset {Offset}",
                hostOverrides.Count, portOffset);
        }

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

    public ServerConnection? GetConnection(int serverType)
    {
        lock (_connections)
            return _connections.FirstOrDefault(c =>
                c.State == ConnectionState.Connected &&
                c.Endpoint.ServerType == serverType);
    }

    public List<ServerConnection> GetConnections(int serverType)
    {
        lock (_connections)
            return _connections
                .Where(c => c.State == ConnectionState.Connected && c.Endpoint.ServerType == serverType)
                .ToList();
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
