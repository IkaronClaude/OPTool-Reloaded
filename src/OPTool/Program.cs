using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using FiestaLibReloaded.Config;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Structs;
using OPTool;

// Notice / chat text is EUC-KR (cp949). Register the legacy code-page provider so
// Encoding.GetEncoding(949) is available on .NET Core.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

// Turn backend query failures into clean HTTP errors instead of raw 500s. A
// SendAndWaitAsync that times out throws TaskCanceledException (the server, e.g.
// WM, didn't answer the OPTOOL request in the wait window); a malformed ACK
// throws EndOfStreamException. Map these to 504 / 502 so callers get a clear
// signal. (Client-initiated aborts are excluded.)
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException
                               && !ctx.RequestAborted.IsCancellationRequested)
    {
        ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        await ctx.Response.WriteAsJsonAsync(new { error = "upstream server did not answer in time", path = ctx.Request.Path.Value });
    }
    catch (EndOfStreamException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new { error = "could not parse upstream server response", path = ctx.Request.Path.Value });
    }
});

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

// Helper: get Login connection (some OPTOOL handlers live on Login, not WM)
static ServerConnection? GetLogin(ConnectionManager cm) =>
    cm.GetConnection((int)FiestaServerType.Login);

// Full OPTOOL opcode = (dept 0x0A << 10) | command. Used for the zero-payload
// REQ/CLR opcodes that have no generated struct (CLOSE_SERVER_REQ,
// WM_SEND_PACKET_STATISTICS_REQ/CLR, LOGON_PROCESS_TIME_VIEW_REQ/CLR,
// KQ_ALL_RESET_CMD).
static ushort OptoolOp(int cmd) => (ushort)((0x0A << 10) | cmd);
static FiestaPacket EmptyOptool(int cmd) => new(OptoolOp(cmd), ReadOnlyMemory<byte>.Empty);

// Encode a string into a fixed-length, null-padded ASCII buffer (truncating if
// over length). Used to fill the Name*/sIntro/sNotify fixed arrays.
static byte[] FixedBytes(string? s, int len)
{
    var buf = new byte[len];
    if (!string.IsNullOrEmpty(s))
    {
        var b = Encoding.ASCII.GetBytes(s);
        Array.Copy(b, buf, Math.Min(b.Length, len));
    }
    return buf;
}
static sbyte[] FixedSBytes(string? s, int len)
{
    var buf = new sbyte[len];
    if (!string.IsNullOrEmpty(s))
    {
        var b = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < Math.Min(b.Length, len); i++) buf[i] = (sbyte)b[i];
    }
    return buf;
}

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

// --- Server Announcement ("GM Say") ---
// NC_ACT_NOTICE_REQ (ACT dept 0x08, cmd 16) -> WM broadcasts NC_ACT_NOTICE_CMD to
// every connected client. Fire-and-forget command (no ACK). World-wide only.
app.MapPost("/api/announce", async (string message, ConnectionManager cm, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(message))
        return Results.BadRequest("message is required");

    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var bytes = Encoding.GetEncoding(949).GetBytes(message);
    if (bytes.Length > byte.MaxValue)
        return Results.BadRequest($"message too long: {bytes.Length} bytes encoded (max {byte.MaxValue})");

    var req = new PROTO_NC_ACT_NOTICE_REQ
    {
        itemLinkDataCount = 0,
        len = (byte)bytes.Length,
        content = bytes,
    };
    await conn.SendAsync(new FiestaPacket(PROTO_NC_ACT_NOTICE_REQ.Opcode, req.ToBytes()), ct);

    return Results.Ok(new { sent = true, bytes = bytes.Length, message });
});

// ============================================================================
// KQ (Kingdom Quest) schedule
// ============================================================================

// --- KQ Schedule (read) ---  REQ 9 -> ACK 10
app.MapGet("/api/kq-schedule", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(new PROTO_NC_OPTOOL_KQ_SCHEDULE_REQ()),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_KQ_SCHEDULE_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_KQ_SCHEDULE_ACK>();

    return Results.Ok(new
    {
        total = ack.NumOfTotal,
        part = ack.bPart,
        start_index = ack.StartDataIndex,
        count = ack.NumOfQuest,
        quests = ack.QuestArray.Select(q => new
        {
            reward_index = q.RewardIndex,
            repeat_mode = q.RepeatMode,
            repeat_count = q.RepeatCount,
            demand_mob_kill = q.DemandMobKill,
            schedule_time = q.ScheduleTime,
            run_counter = q.RunCounter,
            is_team_pvp = q.IsTeamPVP != 0,
        })
    });
});

// --- KQ Map Allocation Info (read) ---  REQ 12 -> ACK 13
app.MapGet("/api/kq-map-alloc", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(new PROTO_NC_OPTOOL_KQ_MAP_ALLOC_INFO_REQ()),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_KQ_MAP_ALLOC_INFO_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_KQ_MAP_ALLOC_INFO_ACK>();

    return Results.Ok(new
    {
        count = ack.nNumOfMapArray,
        maps = ack.MapArray.Select(m => new { allocated_quests = m.AllocatedQuest })
    });
});

// --- KQ Delete (command) ---  CMD 17
app.MapPost("/api/kq-delete", async (uint handle, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    await conn.SendAsync(FiestaPacket.Create(new PROTO_NC_OPTOOL_KQ_DELETE_CMD { Handle = handle }), ct);
    return Results.Ok(new { sent = true, handle });
});

// --- KQ All Reset (command, no struct) ---  CMD 38
app.MapPost("/api/kq-all-reset", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    await conn.SendAsync(EmptyOptool(38), ct);
    return Results.Ok(new { sent = true });
});

// ============================================================================
// WM packet statistics
// ============================================================================

// --- Packet Statistics (read; REQ has no struct) ---  REQ 27 -> ACK 28
app.MapGet("/api/packet-stats", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        EmptyOptool(27),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_WM_SEND_PACKET_STATISTICS_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_WM_SEND_PACKET_STATISTICS_ACK>();

    return Results.Ok(new { part = ack.bPart, data_count = ack.nDataCount });
});

// --- Packet Statistics Clear (command, no struct) ---  CLR 26
app.MapPost("/api/packet-stats/clear", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    await conn.SendAsync(EmptyOptool(26), ct);
    return Results.Ok(new { cleared = true });
});

// ============================================================================
// Guild administration
// ============================================================================

// --- Guild: Change Member Grade ---  REQ 32 -> ACK 33
app.MapPost("/api/guild/member-grade", async (uint guildNo, uint charNo, byte oldGrade, byte newGrade,
    ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_REQ
    {
        nGuildNo = guildNo, nCharNo = charNo, nOldGrade = oldGrade, nNewGrade = newGrade,
    };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_ACK>();

    return Results.Ok(new { guild_no = guildNo, char_no = charNo, error = ack.error });
});

// --- Guild: Cancel Dismiss ---  REQ 43 -> ACK.
// NOTE: WM's dismiss-cancel handler does NOT reply with GUILD_DISMISS_CANCEL_ACK
// (cmd 44); verified live, it answers with the generic GUILD_DATA_CHANGE_ACK
// (cmd 37, 0x2825, a ushort error). Wait for that opcode instead.
app.MapPost("/api/guild/dismiss-cancel", async (uint guildNo, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(new PROTO_NC_OPTOOL_GUILD_DISMISS_CANCEL_REQ { nNo = guildNo }),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_GUILD_DATA_CHANGE_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_GUILD_DATA_CHANGE_ACK>();

    return Results.Ok(new { guild_no = guildNo, error = ack.error });
});

// --- Guild: Reset Tournament Schedule ---  REQ 34 -> ACK 35
app.MapPost("/api/guild/tournament-reset", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(new PROTO_NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_REQ()),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_ACK>();

    return Results.Ok(new { tournament_no = ack.nGTNo });
});

// ============================================================================
// Login server: user ratable & logon process timing
// ============================================================================

// --- Login User Ratable (read) ---  REQ 18 -> ACK 19 (Login)
app.MapGet("/api/login-rate", async (byte? world, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetLogin(cm);
    if (conn is null) return Results.Problem("No connected Login server", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_LOGIN_USER_RATABLE_GET_REQ { nWorldNo = world ?? 0 };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_LOGIN_USER_RATABLE_GET_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_LOGIN_USER_RATABLE_GET_ACK>();

    return Results.Ok(new
    {
        world = ack.nWorldNo,
        susceptibility = new
        {
            s1 = ack.RateTable.nWMSUS_1,
            s2 = ack.RateTable.nWMSUS_2,
            s3 = ack.RateTable.nWMSUS_3,
            s4 = ack.RateTable.nWMSUS_4,
            s5 = ack.RateTable.nWMSUS_5,
            full = ack.RateTable.nWMSUS_FULL,
        }
    });
});

// --- Login User Ratable (set; command) ---  CMD 20 (Login)
app.MapPost("/api/login-rate", async (byte world, ushort s1, ushort s2, ushort s3, ushort s4, ushort s5,
    ushort full, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetLogin(cm);
    if (conn is null) return Results.Problem("No connected Login server", statusCode: 503);

    var cmd = new PROTO_NC_OPTOOL_LOGIN_USER_RATABLE_SET_CMD
    {
        nWorldNo = world,
        RateTable = new LOGIN_USER_RATABLE
        {
            nWMSUS_1 = s1, nWMSUS_2 = s2, nWMSUS_3 = s3,
            nWMSUS_4 = s4, nWMSUS_5 = s5, nWMSUS_FULL = full,
        }
    };
    await conn.SendAsync(FiestaPacket.Create(cmd), ct);
    return Results.Ok(new { set = true, world });
});

// --- Logon Process Time (read; REQ has no struct) ---  REQ 24 -> ACK 25 (Login)
app.MapGet("/api/logon-process-time", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetLogin(cm);
    if (conn is null) return Results.Problem("No connected Login server", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        EmptyOptool(24),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_ACK>();

    return Results.Ok(new
    {
        connect = new { count = ack.Connect_Count, time = ack.Connect_Time },
        login = new { count = ack.Login_Count, time = ack.Login_Time },
        ip_block = new { count = ack.IPBlock_Count, time = ack.IPBlock_Time },
    });
});

// --- Logon Process Time Clear (command, no struct) ---  CLR 23 (Login)
app.MapPost("/api/logon-process-time/clear", async (ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetLogin(cm);
    if (conn is null) return Results.Problem("No connected Login server", statusCode: 503);

    await conn.SendAsync(EmptyOptool(23), ct);
    return Results.Ok(new { cleared = true });
});

// ============================================================================
// Destructive operations -- gated behind an explicit confirm token
// ============================================================================

// --- Character Delete ---  REQ 29 -> ACK 30. Irreversible: deletes a character.
app.MapPost("/api/character-delete", async (uint charNo, bool? confirm, ConnectionManager cm, CancellationToken ct) =>
{
    if (confirm != true)
        return Results.BadRequest(new { error = "destructive: pass ?confirm=true to delete character", char_no = charNo });

    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(new PROTO_NC_OPTOOL_CHARACTER_DELETE_REQ { nCharNo = charNo }),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_CHARACTER_DELETE_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_CHARACTER_DELETE_ACK>();

    return Results.Ok(new { char_no = charNo, status = ack.status });
});

// --- Close Server ---  REQ 3 (no struct) -> ACK 4. Shuts the target server down.
// Requires ?confirm=CLOSE so a stray request can't take the world offline.
app.MapPost("/api/close-server", async (string? confirm, ConnectionManager cm, CancellationToken ct) =>
{
    if (confirm != "CLOSE")
        return Results.BadRequest(new { error = "destructive: pass ?confirm=CLOSE to shut down the WorldManager" });

    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var ackPacket = await conn.SendAndWaitAsync(
        EmptyOptool(3),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_CLOSE_SERVER_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_CLOSE_SERVER_ACK>();

    return Results.Ok(new { closing = true, error = ack.error });
});

// ============================================================================
// Full-record mutations (JSON body). These overwrite an entire record, so the
// caller must supply every field -- there is no read-side to merge against.
// ============================================================================

// --- Guild: Change full guild record ---  REQ 36 -> ACK (GUILD_DATA_CHANGE_ACK 0x2825)
app.MapPost("/api/guild/data-change", async (GuildDataChangeRequest body, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var req = new PROTO_NC_OPTOOL_GUILD_DATA_CHANGE_REQ
    {
        nNo = body.No,
        sName = new Name4 { n4_name = FixedBytes(body.Name, 16) },
        sPassword = new Name3 { n3_name = FixedBytes(body.Password, 12) },
        nMoney = body.Money,
        nType = body.Type,
        nGrade = body.Grade,
        nFame = body.Fame,
        nStoneLevel = body.StoneLevel,
        nExp = body.Exp,
        nMaxMembers = body.MaxMembers,
        nWarWinCount = body.WarWin,
        nWarLoseCount = body.WarLose,
        nWarDrawCount = body.WarDraw,
        nDismissStatus = body.DismissStatus,
        dDismissDate = body.DismissDate,
        dNotifyDate = body.NotifyDate,
        sNotifyCharID = new Name5 { n5_name = FixedBytes(body.NotifyCharId, 20) },
        sIntro = FixedSBytes(body.Intro, 128),
        sNotify = FixedSBytes(body.Notify, 512),
    };
    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(req),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_GUILD_DATA_CHANGE_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_GUILD_DATA_CHANGE_ACK>();

    return Results.Ok(new { guild_no = body.No, error = ack.error });
});

// --- KQ: Change/add a Kingdom Quest definition ---  CMD 11 (fire-and-forget).
// Mutates the live KQ schedule; nested MapLink[4]/TeamRegenXY[2] default to
// empty unless you need them. No ACK from WM.
app.MapPost("/api/kq-change", async (KqChangeRequest body, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var kq = new PROTO_KQ_INFO
    {
        NextStartMode = body.NextStartMode,
        NextStartDelayMin = body.NextStartDelayMin,
        RepeatMode = body.RepeatMode,
        RepeatCount = body.RepeatCount,
        RewardIndex = body.RewardIndex,
        DemandMobKill = body.DemandMobKill,
        ScheduleTime = body.ScheduleTime,
        RunCounter = body.RunCounter,
        ScriptLanguage = FixedBytes(body.ScriptLanguage, 32),
        ScriptInitValue = FixedBytes(body.ScriptInitValue, 32),
        IsTeamPVP = (byte)(body.IsTeamPvp ? 1 : 0),
    };
    for (int i = 0; i < kq.MapLink.Length; i++) kq.MapLink[i] = new PROTO_KQ_MAP_INFO();
    for (int i = 0; i < kq.TeamRegenXY.Length; i++) kq.TeamRegenXY[i] = new SHINE_XY_TYPE();

    await conn.SendAsync(FiestaPacket.Create(new PROTO_NC_OPTOOL_KQ_CHANGE_CMD { KQInfo = kq }), ct);
    return Results.Ok(new { sent = true, reward_index = body.RewardIndex });
});

// --- Guild: Change tournament schedule/bracket ---  REQ 21 -> ACK (0x2816).
// TournamentTree[31] defaults to empty entries; the time fields are the knobs.
app.MapPost("/api/guild/tournament-change", async (TournamentChangeRequest body, ConnectionManager cm, CancellationToken ct) =>
{
    var conn = GetWm(cm);
    if (conn is null) return Results.Problem("No connected WorldManager", statusCode: 503);

    var cmd = new PROTO_NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_CMD
    {
        nMatchType = body.MatchType,
        Time_Start = body.TimeStart,
        Time_Practic = body.TimePractic,
        Time_PracticEnd = body.TimePracticEnd,
        Time_Match_161 = body.TimeMatch161,
        Time_Match_162 = body.TimeMatch162,
        Time_Match_8 = body.TimeMatch8,
        Time_Match_4 = body.TimeMatch4,
        Time_Match_2 = body.TimeMatch2,
        Time_Match_End = body.TimeMatchEnd,
    };
    for (int i = 0; i < cmd.TournamentTree.Length; i++) cmd.TournamentTree[i] = new GUILD_TOURNAMENT_LIST();

    var ackPacket = await conn.SendAndWaitAsync(
        FiestaPacket.Create(cmd),
        PacketRegistry.GetOpcode<PROTO_NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_ACK>(),
        TimeSpan.FromSeconds(5), ct);
    var ack = ackPacket.ReadBody<PROTO_NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_ACK>();

    return Results.Ok(new { match_type = body.MatchType, error = ack.error });
});

app.Run();

// Request DTOs for the full-record mutation endpoints (bound from JSON body).
record GuildDataChangeRequest(
    uint No, string? Name, string? Password, ulong Money, byte Type, byte Grade,
    uint Fame, ushort StoneLevel, ulong Exp, ushort MaxMembers,
    uint WarWin, uint WarLose, uint WarDraw, byte DismissStatus,
    int DismissDate, int NotifyDate, string? NotifyCharId, string? Intro, string? Notify);

record KqChangeRequest(
    byte NextStartMode, ushort NextStartDelayMin, byte RepeatMode, ushort RepeatCount,
    ushort RewardIndex, ushort DemandMobKill, int ScheduleTime, byte RunCounter,
    string? ScriptLanguage, string? ScriptInitValue, bool IsTeamPvp);

record TournamentChangeRequest(
    byte MatchType, int TimeStart, int TimePractic, int TimePracticEnd,
    int TimeMatch161, int TimeMatch162, int TimeMatch8, int TimeMatch4,
    int TimeMatch2, int TimeMatchEnd);
