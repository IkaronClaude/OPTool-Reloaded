using FiestaLibReloaded.Config;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Structs;
using OPTool;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionManager>());

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.MapGet("/status", (ConnectionManager cm) => cm.Status);

app.MapGet("/api/s2s-list", async (ConnectionManager cm, CancellationToken ct) =>
{
    // Find a connected WorldManager (ServerType 5)
    var conn = cm.GetConnection((int)FiestaServerType.WorldManager);
    if (conn is null)
        return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_S2SCONNECT_LIST_REQ { echo_data = 1 };
    var reqPacket = FiestaPacket.Create(req);
    var ackOpcode = PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_S2SCONNECT_LIST_ACK>();

    var ackPacket = await conn.SendAndWaitAsync(reqPacket, ackOpcode, TimeSpan.FromSeconds(5), ct);
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

app.Run();
