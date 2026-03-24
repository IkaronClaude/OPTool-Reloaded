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