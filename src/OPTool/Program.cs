using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using FiestaLibReloaded.Config;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Structs;
using OPTool;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionManager>());
builder.Services.AddOpenApi();

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

var pathBase = Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
if (!string.IsNullOrEmpty(pathBase))
    app.UsePathBase(pathBase);

if (Environment.GetEnvironmentVariable("DISABLE_SWAGGER") != "true")
{
    app.MapOpenApi();
    var swaggerPrefix = string.IsNullOrEmpty(pathBase) ? "" : pathBase;
    app.UseSwaggerUI(o => o.SwaggerEndpoint($"{swaggerPrefix}/openapi/v1.json", "OPTool API"));
}

// Helper: extract null-terminated string from fixed byte array
static string CStr(byte[] buf)
{
    var end = Array.IndexOf(buf, (byte)0);
    return Encoding.ASCII.GetString(buf, 0, end < 0 ? buf.Length : end);
}

// Helper: get WM connection or return 503
static ServerConnection? GetWm(ConnectionManager cm) =>
    cm.GetConnection((int)FiestaServerType.WorldManager);

app.MapGet("/health", () => "ok");

app.MapGet("/status", (ConnectionManager cm) => cm.Status);

// --- S2S Connection List ---
app.MapGet("/api/s2s-list", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_S2SCONNECT_LIST_REQ { echo_data = 1 };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_S2SCONNECT_LIST_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_S2SCONNECT_LIST_ACK>();

    return Results.Ok(new
    {
        echo_data = ack.echo_data,
        server_id = ack.my_server_id,
        connections = ack.connection_info.Select(c => new
        {
            world = c.connect_server_world,
            zone = c.connect_server_zone,
            server_id = c.connect_server_id,
        })
    });
});

// --- Find User ---
app.MapGet("/api/find-user", async (int userId, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_FIND_USER_REQ { nUserNo = (uint)userId };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_FIND_USER_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_FIND_USER_ACK>();

    return Results.Ok(new
    {
        user_no = ack.nUserNo,
        is_login = ack.bIsLogin != 0,
        user_id = CStr(ack.sUserID.n256_name),
        char_no = ack.nCharNo,
        char_name = CStr(ack.sCharID.n5_name),
        map_name = CStr(ack.sMapName.n3_name),
    });
});

// --- Kick User ---
app.MapPost("/api/kick-user", async (int userId, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_KICK_USER_REQ { nUserNo = (uint)userId };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_KICK_USER_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_KICK_USER_ACK>();

    return Results.Ok(new
    {
        user_no = ack.nUserNo,
        kicked = ack.bKick != 0,
        user_id = CStr(ack.sUserID.n256_name),
        char_no = ack.nCharNo,
        char_name = CStr(ack.sCharID.n5_name),
        map_name = CStr(ack.sMapName.n3_name),
    });
});

// --- Map User List ---
app.MapGet("/api/map-users", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_MAP_USER_LIST_REQ { echo_data = 1 };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_MAP_USER_LIST_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_MAP_USER_LIST_ACK>();

    return Results.Ok(new
    {
        server_id = ack.my_server_id,
        maps = ack.user_info.Select(m => new
        {
            map_id = m.map_id,
            map_name = CStr(m.map_name.n3_name),
            user_count = m.num_of_user,
        })
    });
});

// --- Connection Brief ---
app.MapGet("/api/connect-brief", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_CONNECT_BRIF_REQ { echo_data = 1 };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_CONNECT_BRIF_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_CONNECT_BRIF_ACK>();

    return Results.Ok(new
    {
        server_id = ack.my_server_id,
        counts = ack.count,
    });
});

// --- Get User Limit ---
app.MapGet("/api/user-limit", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_REQ_CLIENT_NUM_OF_USER_LIMIT();
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_ACK_CLIENT_NUM_OF_USER_LIMIT>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_ACK_CLIENT_NUM_OF_USER_LIMIT>();

    return Results.Ok(new
    {
        world = ack.WorldNo,
        user_limit = ack.NumOfUserLimit,
        max = ack.NumOfMax,
    });
});

// --- Set User Limit ---
app.MapPost("/api/user-limit", async (int limit, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var cmd = new PROTO_NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT { NumOfUserLimit = limit };
    await conn.SendAsync(FiestaPacket.Create(cmd), ct);

    return Results.Ok(new { set_limit = limit });
});

app.Run();
