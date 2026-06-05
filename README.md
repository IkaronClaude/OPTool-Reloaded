# OPTool-Reloaded

Fiesta Online server operations tool — a REST API that connects to the game's WorldManager process and exposes admin operations (find/kick users, server status, connection info) over HTTP.

## Quick Start

```bash
dotnet run --project src/OPTool/OPTool.csproj
```

## Docker

```bash
docker compose up -d
```

## HTTP API

| Method & path | Purpose |
|---------------|---------|
| `GET /health` | Liveness probe |
| `GET /status` | Per-endpoint connection state |
| `GET /api/s2s-list` | WM's s2s connection list |
| `GET /api/find-user?userId=` | Locate a user (login state, char, map) |
| `POST /api/kick-user?userId=` | Kick a user |
| `GET /api/map-users` | Per-map user counts |
| `GET /api/connect-brief` | Connection brief counts |
| `GET` / `POST /api/user-limit` | Read / set the client user limit |
| `POST /api/announce?message=` | **GM Say** — world-wide server notice |

`POST /api/announce` sends `NC_ACT_NOTICE_REQ` (ACT dept, cmd 16) to WorldManager,
which broadcasts `NC_ACT_NOTICE_CMD` to every connected client. The message is
encoded as EUC-KR (cp949) and must be ≤ 255 bytes encoded. The broadcast is
world-wide — there is no per-zone target in this protocol path.

## Swagger UI

Swagger is enabled by default. Set `DISABLE_SWAGGER=true` to turn it off.

Access at `/swagger` (or `/<pathbase>/swagger` behind a reverse proxy).

## Reverse Proxy Configuration

When running behind nginx or another reverse proxy on a subpath (e.g. `/optool/`), set these environment variables on the container:

| Variable | Purpose | Example |
|----------|---------|---------|
| `ASPNETCORE_PATHBASE` | Subpath prefix — the app strips this for routing and includes it in generated URLs | `/optool` |
| `DISABLE_SWAGGER` | Set to `true` to disable Swagger UI | `true` |

The app includes `UseForwardedHeaders` middleware that trusts `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` headers from any proxy. This ensures the OpenAPI spec generates correct URLs (e.g. `https://` instead of `http://`).

### Example nginx config

```nginx
location /optool/ {
    proxy_pass http://127.0.0.1:5160;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
}
```

**Important:** Do not add a trailing slash to `proxy_pass` — the full path (including `/optool/`) must reach the app so `UsePathBase` can strip it correctly.

### docker-compose.yml

```yaml
services:
  optool:
    build: .
    network_mode: host
    environment:
      - ASPNETCORE_PATHBASE=/optool
      - Fiesta__ServerInfoPath=/data/9Data/ServerInfo/ServerInfo.txt
    volumes:
      - /path/to/server/files:/data:ro
```

## Kubernetes / s2s-proxy stacks

By default OPTool connects to the literal `IP:port` in each OpTool row of
`ServerInfo.txt`. That's correct for a flat deployment, but in a proxied stack
(e.g. [fiesta-docker](https://github.com/IkaronClaude/fiesta-docker) on k8s)
those peer rows are rewritten to `127.0.0.1` (each pod's local s2s proxy) and are
meaningless from a separate OpTool pod.

Two optional, additive config keys make OPTool addressable in that topology
(both default to no-ops, so flat / non-s2s servers are unaffected):

| Key | Purpose |
|-----|---------|
| `Fiesta:EndpointHostOverrides:<ServerInfoRowName>` | Remap a row's host to an in-cluster DNS name. Key is the row's `ServerInfo` label (e.g. `PG_W00_WM`), as logged at startup. |
| `Fiesta:S2sPortOffset` | Added to every endpoint port. Set to your `S2S_INTERNAL_OFFSET` (default `10000`) to dial the target pod's **proxy inbound** listener, so the exe sees the connection as `127.0.0.1` and its OpTool source-IP check passes. Leave `0` to hit the original OpTool port directly. |

The OpTool/s2s side needs **no public ingress** — OPTool runs in-cluster and
reaches peers pod→pod over the existing headless Services. Only OPTool's HTTP API
needs to be exposed (admin-only). Example env (one host override per server):

```yaml
env:
  - name: Fiesta__ServerInfoPath
    value: /data/9Data/ServerInfo/ServerInfo.txt
  - name: Fiesta__S2sPortOffset            # 0 = direct OpTool port; 10000 = via target pod's proxy
    value: "0"
  - name: Fiesta__EndpointHostOverrides__PG_Login
    value: login.fiesta.svc.cluster.local
  - name: Fiesta__EndpointHostOverrides__PG_W00_WM
    value: worldmanager.fiesta.svc.cluster.local
  - name: Fiesta__EndpointHostOverrides__PG_W00_Z00
    value: zone00.fiesta.svc.cluster.local
  # ...one per zone
```