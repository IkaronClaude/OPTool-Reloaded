# Project Ideas

## Fiesta Filter Lib

TCP tunnel that reads Fiesta packets like a Fiesta-specific proxy. Rewrites "announcement" packets (server address broadcasts) so clients connect through the tunnel.

**Server mode**: Reads `ServerInfo.txt` to automatically know what to proxy and where.

**Goal**: TLS encryption of game traffic (unencrypted by default, MD5 password, horrible) and hack prevention.

**Architecture**: Plugin-based layered pipeline:
- Plugins are loaded as DLLs and analyse/transform traffic on the fly
- **Port rewrite plugin** (low layer) - finds announcement packets and rewrites them to route through the proxy
- **Chat sanitizer plugin** - cleans chat packets containing illegal symbols
- **TLS plugin** (final layer) - encrypts/decrypts traffic at the outermost boundary

## Fiesta Hooking

DLL injection and binary patching helper library.

**Goal**: Fix exploits and bugs inside server executables (e.g. `Zone.exe`) and the client (`Fiesta.bin`).

## Fiesta Bot

Uses FiestaLib to mimic a real game client.

**Use cases**:
- Simple: Run around in town buffing players
- Advanced: Spawn bot instances that play alongside real players when the server is empty

## Unity Fiesta Client Rewrite

Rewrite the Fiesta Online client in Unity.

## OPTool Web UI

Admin web interface for OPTool (currently Swagger only).
