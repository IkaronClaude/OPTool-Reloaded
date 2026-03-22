# OPTool Reloaded — Tracker

## Goal

Re-create the Fiesta Online Operator Tool as an ASP.NET Core web API with a frontend.
The original OpTool exe is lost — we're building from binary analysis of the server executables.

## Architecture

```
                    ┌─────────────────────┐
                    │   Frontend (web UI)  │
                    └─────────┬───────────┘
                              │
┌──────────────┐    ┌─────────┴───────────┐
│  CLI / cURL  │────│  ASP.NET Core API   │  ← OAuth2/JWT auth
│  (deploy CI) │    │  /optool/ endpoints │
└──────────────┘    └─────────┬───────────┘
                              │
                    ┌─────────┴───────────┐
                    │  FiestaProtocol lib  │  ← shared NuGet package
                    │  (packet defs,       │
                    │   framing, structs)  │
                    └─────────┬───────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        │                     │                      │
  [Login:9012]        [WM:9015]              [Zone00:9018] ...
```

### Solution Structure
```
OPTool-Reloaded.sln
  src/
    FiestaProtocol/           ← Class library: packets, structs, framing, departments
    OPTool.Api/               ← ASP.NET Core Web API: endpoints, auth, connection mgmt
  docs/
    handler-addresses.md      ← Function addresses per EXE for future emulator work
```

### Key Design Decisions

- **Auth**: OAuth2/JWT — API must be callable from CLI (deploy scripts, CI/CD)
- **FiestaProtocol is a separate library** — reusable for future emulator projects,
  open-sourceable as a community resource with full packet/struct definitions
- **CLI-first use case**: deploy script calls API to announce, wait, shutdown, deploy:
  ```bash
  mimir build --all
  curl -H "Authorization: Bearer $TOKEN" -X POST $API/optool/announce \
       -d '{"message": "Servers will restart in 5 minutes"}'
  sleep 300
  curl -H "Authorization: Bearer $TOKEN" -X POST $API/optool/shutdown
  docker compose stop ...
  # deploy files
  docker compose up -d ...
  ```
- **Frontend**: Swagger for initial testing, then a simple admin web UI
- **Deployment**: reverse proxy via nginx on VPS

---

## Priority Tiers

### P0 — Foundation (must have first)

- [ ] **Project scaffolding** — ASP.NET Core Web API project, solution file
- [ ] **Packet framing** — length-prefixed binary protocol (1-byte or 3-byte length header)
- [ ] **S2S handshake** — NC_MISC_S2SCONNECTION_RDY/REQ/ACK (0x0801/02/03)
- [ ] **Heartbeat** — NC_MISC_HEARTBEAT_REQ/ACK (0x0808) keepalive
- [ ] **Connection manager** — connect to WM/Login/Zone OpTool ports, reconnect on failure

### P1 — Server Announcements (primary use case)

- [ ] **NC_ACT_NOTICE_REQ** — send server-wide announcement via WM:9015
- [ ] **API endpoint**: `POST /optool/announce` with message body
- [ ] **Verify**: client receives announcement in-game

### P2 — Server Management

- [ ] **NC_OPTOOL_CLOSE_SERVER_REQ** (cmd 7) — graceful server shutdown
- [ ] **NC_OPTOOL_KICK_USER_REQ** (cmd 9) — force-disconnect player
- [ ] **NC_OPTOOL_FIND_USER_REQ** (cmd 11) — find player by name
- [ ] **NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT** (cmd 27) — change player cap
- [ ] **NC_OPTOOL_CONNECT_BRIF_REQ** (cmd 1) — get connection status brief

### P3 — Monitoring & Info

- [ ] **NC_OPTOOL_MAP_USER_LIST_REQ** (cmd 3) — list users per map
- [ ] **NC_OPTOOL_S2SCONNECT_LIST_REQ** (cmd 5) — list S2S connections
- [ ] **NC_OPTOOL_WM_SEND_PACKET_STATISTICS_REQ** (cmd 29) — packet stats
- [ ] **NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_REQ** (cmd 23) — logon timing
- [ ] **NC_OPTOOL_REQ_CLIENT_NUM_OF_USER_LIMIT** (cmd 26) — get player cap

### P4 — Character & Guild Management

- [ ] **NC_OPTOOL_CHARACTER_DELETE_REQ** (cmd 12) — delete character
- [ ] **NC_OPTOOL_GUILD_DATA_CHANGE_REQ** (cmd 14) — modify guild data
- [ ] **NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_REQ** (cmd 15) — change member rank
- [ ] **NC_OPTOOL_GUILD_DISMISS_CANCEL_REQ** (cmd 21) — cancel guild dissolution
- [ ] **NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_CMD** (cmd 43) — tournament changes
- [ ] **NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_REQ** (cmd 41) — reset tournament

### P5 — Kingdom Quest Management

- [ ] **NC_OPTOOL_KQ_SCHEDULE_REQ** (cmd 32) — get KQ schedule
- [ ] **NC_OPTOOL_KQ_CHANGE_CMD** (cmd 34) — modify KQ
- [ ] **NC_OPTOOL_KQ_DELETE_CMD** (cmd 36) — delete KQ
- [ ] **NC_OPTOOL_KQ_ALL_RESET_CMD** (cmd 38) — reset all KQs
- [ ] **NC_OPTOOL_KQ_MAP_ALLOC_INFO_REQ** (cmd 39) — KQ map allocation

### P6 — Event Management (via WM OpTool parser, non-OPTOOL department)

- [ ] **NC_EVENT_ADD_EVENT_REQ** — add event
- [ ] **NC_EVENT_DEL_EVENT_REQ** — delete event
- [ ] **NC_EVENT_UPDATE_EVENT_REQ** — update event
- [ ] **NC_EVENT_GET_ALL_EVENT_INFO_REQ** — list all events
- [ ] **NC_EVENT_SET_ALL_READY_REQ** — activate all events

### P7 — Login-Specific (connect to Login:9012)

- [ ] **NC_OPTOOL_LOGIN_USER_RATABLE_GET_REQ** (cmd 18) — get rate table
- [ ] **NC_OPTOOL_LOGIN_USER_RATABLE_SET_CMD** (cmd 20) — set rate table
- [ ] **NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_REQ** (cmd 23) — logon timing
- [ ] **NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_CLR** (cmd 24) — clear logon timing

---

## FiestaProtocol Library

### Vision

A standalone, open-source C# class library containing:
1. **Complete department + opcode enum** — every NC_* packet ID with department and command bytes
2. **Packet struct definitions** — C# records/classes matching every PROTO_NC_* struct from the PDBs
3. **Packet framing** — read/write the length-prefixed wire format
4. **Serialization** — binary reader/writer for each struct
5. **Handler address annotations** — function addresses per EXE for future disassembly/emulator work

This is the community resource: a complete machine-readable map of the Fiesta protocol,
extracted from the actual server binaries via PDB analysis.

### PDB Extraction Approach

PDB files are MSF 7.00 format with full TPI (Type Program Information) stream.
The TPI stream contains `LF_FIELDLIST` records with packet IDs (e.g. `NC_HOLY_PROMISE_SET_UP_CONFIRM_REQ 4`)
and `LF_STRUCTURE`/`LF_CLASS` records with field offsets, types, and sizes.

**Tool**: `llvm-pdbutil` (LLVM 22.1.1 at `C:\Program Files\LLVM\bin\`)
```bash
# Dump all type records
llvm-pdbutil dump -types "Z:/ServerSource/WorldManager/WorldManager.pdb"

# The output contains LF_FIELDLIST entries like:
#   - data member [NC_HOLY_PROMISE_SET_UP_CONFIRM_REQ, 0x4, ...] — gives us cmd=4
#   - struct PROTO_NC_OPTOOL_KICK_USER_REQ { nUserNo: int, ... } — gives us payload layout
```

### Extraction Pipeline

1. **Extract all LF_ENUM records** — these contain department enums (PROTOCOL_COMMAND_OPTOOL, etc.)
   with explicit numeric values for each opcode name → gives us the complete dept+cmd mapping
2. **Extract all LF_STRUCTURE/LF_CLASS records** matching `PROTO_NC_*` — gives us packet structs
   with field names, types, offsets, and total sizes
3. **Code-generate C# types** — one record per PROTO struct, with `[FieldOffset]` attributes
4. **Code-generate serializers** — binary read/write methods per struct
5. **Generate handler stubs** — method signatures matching `fc_NC_*` patterns from PDB

### Struct Extraction Priority

**P0 — S2S handshake:**
- [ ] PROTO_NC_MISC_S2SCONNECTION_RDY (sent by server on accept)
- [ ] PROTO_NC_MISC_S2SCONNECTION_REQ (sent by OpTool to identify)
- [ ] PROTO_NC_MISC_S2SCONNECTION_ACK (confirmation)
- [ ] PROTO_NC_MISC_HEARTBEAT_REQ / ACK

**P1 — Announcements:**
- [ ] PROTO_NC_ACT_NOTICE_REQ (message text → WM)
- [ ] PROTO_NC_ACT_NOTICE_CMD (WM → clients, for reference)

**P2 — Server management:**
- [ ] PROTO_NC_OPTOOL_CLOSE_SERVER_ACK
- [ ] PROTO_NC_OPTOOL_KICK_USER_REQ / ACK
- [ ] PROTO_NC_OPTOOL_FIND_USER_REQ / ACK
- [ ] PROTO_NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT
- [ ] PROTO_NC_OPTOOL_CONNECT_BRIF_REQ / ACK

**P3+ — Everything else:**
- [ ] All PROTO_NC_OPTOOL_* structs (32 unique)
- [ ] PROTO_NC_EVENT_* structs (5 event management)
- [ ] ALL departments and ALL packets — the full protocol map

### Handler Address Annotations

Store in `docs/handler-addresses.md` — for each `fc_NC_*` function:
- EXE name, handler address (VA), file offset
- Corresponding opcode (department + command)
- PDB source path (original .cpp/.h)

This allows future disassembly work: open IDA/Ghidra at address X to understand
how a specific packet is processed → enables emulator implementation.

---

## Protocol Reference

### Packet Format
```
[Length] [Opcode: 2 bytes] [Payload]

Length encoding:
  1-254:   1 byte (value = length of opcode+payload)
  255+:    0x00 + 2 bytes big-endian (total length of opcode+payload)

Opcode: 2 bytes
  byte 0 = department
  byte 1 = command within department

S2S traffic is unencrypted (no XOR/seed).
```

### All Departments (from handler registration scanning across all EXEs)
| Dep | Hex  | Name              | Header File          | Handler Count (L/WM/Z/A) |
|-----|------|-------------------|----------------------|--------------------------|
| 1   | 0x01 | (unknown)         |                      | -/-/1/-                  |
| 2   | 0x02 | MISC (S2S-login)  | protomisc.h          | 9/38/21/10               |
| 3   | 0x03 | AVATAR            | protoavatar.h        | 24/14/5/13               |
| 4   | 0x04 | CHAR (DB)         | protochar.h          | -/32/32/-                |
| 5   | 0x05 | CHAR              | protochar.h          | -/10/-/2                 |
| 6   | 0x06 | ITEM              | protoitem.h          | -/4/10/-                 |
| 7   | 0x07 | MAP               | protomap.h           | -/-/1/-                  |
| 8   | 0x08 | MISC              | protomisc.h          | -/4/35/-                 |
| 9   | 0x09 | ACT               | protoact.h           | -/2/9/-                  |
| 10  | 0x0A | **OPTOOL**        | protooptool.h        | 8/22/1/4                 |
| 12  | 0x0C | ITEM (DB)         | protoitemdb.h        | -/-/49/-                 |
| 13  | 0x0D | MOB/NPC           | protomover.h?        | -/1/68/-                 |
| 14  | 0x0E | GUILD             | protoguild.h         | -/14/12/-                |
| 15  | 0x0F | BRIEFINFO         | protobriefinfo.h     | -/-/1/-                  |
| 16  | 0x10 | PARTY             | protoparty.h         | 1/22/12/1                |
| 17  | 0x11 | TRADE             | prototrade.h?        | -/-/15/-                 |
| 18  | 0x12 | BOOTH             | protobooth.h?        | -/1/10/-                 |
| 19  | 0x13 | QUEST             | protoquest.h?        | -/-/10/-                 |
| 20  | 0x14 | KQ                | protokingdomquest.h  | -/2/5/-                  |
| 21  | 0x15 | FRIEND            | protofriend.h        | -/12/1/-                 |
| 22  | 0x16 | GUILD2            | protoguild.h         | -/13/8/-                 |
| 23  | 0x17 | MINIHOUSE         | protominihouse.h     | -/-/5/-                  |
| 24  | 0x18 | AUCTION           | protoauction.h       | -/1/5/-                  |
| 26  | 0x1A | INSTANCE_DUNGEON  | protoinstancedungeon.h | -/1/8/-                |
| 27  | 0x1B | DICE/TAISAI       | protodicetaisai.h    | -/-/2/-                  |
| 28  | 0x1C | CHARGED           | protocharged.h       | -/37/-/-                 |
| 29  | 0x1D | CHATRESTRICT      | protochatrestrict.h  | -/43/8/-                 |
| 31  | 0x1F | HOLY PROMISE      | protoholypromise.h   | -/8/2/-                  |
| 32  | 0x20 | GAMBLE            | protogamble.h        | -/7/1/-                  |
| 35  | 0x23 | PET               |                      | -/-/27/-                 |
| 36  | 0x24 | CHAR TITLE        | protocharactertitle.h | -/3/7/-                 |
| 37  | 0x25 | GUILD ACADEMY     | protoguildacademy.h  | -/14/7/-                 |
| 38  | 0x26 | COLLECT           | protocollect.h       | -/44/12/-                |

Header files confirmed from PDB source paths. Handler counts = Login/WM/Zone/Account.

### S2S Handshake Flow
```
1. Server accepts TCP connection
2. Server → OpTool:  NC_MISC_S2SCONNECTION_RDY   (0x0801)
3. OpTool → Server:  NC_MISC_S2SCONNECTION_REQ   (0x0802)
   Payload: server_from_id, world, zone, server_id_to, key, my_type
   my_type = SERVER_ID_OPTOOL
4. Server → OpTool:  NC_MISC_S2SCONNECTION_ACK   (0x0803)
5. Keepalive:        NC_MISC_HEARTBEAT_REQ        (0x0808) / ACK
```

### Announcement Flow (GM Say)
```
1. OpTool connects to WM:9015, completes S2S handshake
2. OpTool → WM:  NC_ACT_NOTICE_REQ  (dep=0x09, cmd=TBD)
3. WM → all clients:  NC_ACT_NOTICE_CMD
```

NC_ACT_NOTICE_REQ is handled by `CParserOPTool::fc_NC_ACT_NOTICE_REQ` in WorldManager.
It is NOT in the OPTOOL department — it's in ACT (dep 9), but WM's OpTool parser
registers a cross-department handler for it.

### OpTool Ports (from ServerInfo.txt type 8)
| Service       | Port  |
|---------------|-------|
| Login         | 9012  |
| WorldManager  | 9015  |
| Zone00        | 9018  |
| Zone01        | 9021  |
| Zone02        | 9024  |
| Zone03        | 9027  |
| Zone04        | 9030  |

### OPTOOL Opcode → Cmd Mapping (dep=0x0A)
| Cmd | Hex  | Opcode                                          | Handled By       |
|-----|------|-------------------------------------------------|------------------|
| 0   | 0x00 | NC_OPTOOL_NULL                                  | -                |
| 1   | 0x01 | NC_OPTOOL_CONNECT_BRIF_REQ                      | Login, WM, Acct  |
| 2   | 0x02 | NC_OPTOOL_CONNECT_BRIF_ACK                      | (response)       |
| 3   | 0x03 | NC_OPTOOL_MAP_USER_LIST_REQ                     | Login, WM, Acct  |
| 4   | 0x04 | NC_OPTOOL_MAP_USER_LIST_ACK                     | (response)       |
| 5   | 0x05 | NC_OPTOOL_S2SCONNECT_LIST_REQ                   | Login, WM, Acct  |
| 6   | 0x06 | NC_OPTOOL_S2SCONNECT_LIST_ACK                   | (response)       |
| 7   | 0x07 | NC_OPTOOL_CLOSE_SERVER_REQ                      | Login, WM, Acct, Zone |
| 8   | 0x08 | NC_OPTOOL_CLOSE_SERVER_ACK                      | (response)       |
| 9   | 0x09 | NC_OPTOOL_KICK_USER_REQ                         | WM               |
| 10  | 0x0A | NC_OPTOOL_KICK_USER_ACK                         | (response)       |
| 11  | 0x0B | NC_OPTOOL_FIND_USER_REQ                         | WM               |
| 12  | 0x0C | NC_OPTOOL_CHARACTER_DELETE_REQ                  | WM               |
| 13  | 0x0D | NC_OPTOOL_CHARACTER_DELETE_ACK                  | (response)       |
| 14  | 0x0E | NC_OPTOOL_GUILD_DATA_CHANGE_REQ                | WM               |
| 15  | 0x0F | NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_REQ        | WM               |
| 17  | 0x11 | NC_OPTOOL_CHARACTER_DELETE_CMD                   | WM               |
| 18  | 0x12 | NC_OPTOOL_LOGIN_USER_RATABLE_GET_REQ            | Login            |
| 19  | 0x13 | NC_OPTOOL_LOGIN_USER_RATABLE_GET_ACK            | (response)       |
| 20  | 0x14 | NC_OPTOOL_LOGIN_USER_RATABLE_SET_CMD            | Login            |
| 21  | 0x15 | NC_OPTOOL_GUILD_DISMISS_CANCEL_REQ              | WM               |
| 23  | 0x17 | NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_REQ           | Login            |
| 24  | 0x18 | NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_CLR           | Login            |
| 26  | 0x1A | NC_OPTOOL_REQ_CLIENT_NUM_OF_USER_LIMIT          | WM               |
| 27  | 0x1B | NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT          | WM               |
| 29  | 0x1D | NC_OPTOOL_WM_SEND_PACKET_STATISTICS_REQ         | WM               |
| 31  | 0x1F | NC_OPTOOL_WM_SEND_PACKET_STATISTICS_CLR         | WM               |
| 32  | 0x20 | NC_OPTOOL_KQ_SCHEDULE_REQ                       | WM               |
| 34  | 0x22 | NC_OPTOOL_KQ_CHANGE_CMD                         | WM               |
| 36  | 0x24 | NC_OPTOOL_KQ_DELETE_CMD                          | WM               |
| 38  | 0x26 | NC_OPTOOL_KQ_ALL_RESET_CMD                      | WM               |
| 39  | 0x27 | NC_OPTOOL_KQ_MAP_ALLOC_INFO_REQ                 | WM               |
| 41  | 0x29 | NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_REQ   | WM               |
| 43  | 0x2B | NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_CMD           | WM               |

### Additional WM OpTool parser handlers (non-OPTOOL department)
These are handled by `CParserOPTool` in WorldManager despite being in other departments:

| Opcode                                  | Department |
|-----------------------------------------|------------|
| NC_ACT_NOTICE_REQ                       | ACT (9)    |
| NC_EVENT_ADD_EVENT_REQ                  | EVENT      |
| NC_EVENT_DEL_EVENT_REQ                  | EVENT      |
| NC_EVENT_UPDATE_EVENT_REQ               | EVENT      |
| NC_EVENT_GET_ALL_EVENT_INFO_REQ         | EVENT      |
| NC_EVENT_SET_ALL_READY_REQ              | EVENT      |
| NC_HOLY_PROMISE_DB_DEL_CHAR_ACK         | HOLY       |
| NC_MISC_PINGTEST_TOOL_WM_*             | MISC       |
| NC_MISC_XTRAP2_OPTOOL_READ_CODEMAP_REQ | MISC       |
| NC_PARTY_MEMBERINFORM_REQ              | PARTY      |

---

## Source Code Origin (from PDB paths)

```
E:\ProjectF2\
  CSCode\Protocol\
    protooptool.h        — shared PROTO_NC_OPTOOL_* struct definitions
    protoact.h           — PROTO_NC_ACT_NOTICE_* structs
    protomisc.h          — PROTO_NC_MISC_S2S* structs
    protocol.h           — master protocol header

  Server\3LoginServer2\Source\
    Protocol\pf_optool.cpp/.h
    Sessions\loginoptoolsession.cpp/.h
    Sessions\loginoptoolsessionmanager.cpp/.h

  Server\4WorldManagerServer2\Source\
    Protocol\pf_optool.cpp/.h          (22 handlers — most extensive)
    Sessions\wmoptoolsession.cpp/.h
    Sessions\wmoptoolsessionmanager.cpp/.h

  Server\5ZoneServer2\
    optoolsession.cpp/.h               (minimal, at root level)
    pf_optool.cpp
```

### Session Classes
| Process       | Session Class              | Parser Class   |
|---------------|---------------------------|----------------|
| Login         | CLoginOPToolSession       | CParserOPTool  |
| WorldManager  | CWMOPToolSession          | CParserOPTool  |
| Zone          | OPToolSession             | (inline)       |
| Account       | (uses shared CPFs)        | CPFs           |
| AccountLog    | (uses shared CPFs)        | CPFs           |

### CParser handler signature
```cpp
// Login
int CParserOPTool::fc_NC_OPTOOL_*(CLoginOPToolSession*, NETPACKET*, int)
// WorldManager
int CParserOPTool::fc_NC_OPTOOL_*(CWMOPToolSession*, NETPACKET*, int)
// Zone (different pattern)
void OPToolSession::opts_NC_OPTOOL_*(NETCOMMAND*)
```

---

## Tooling

- **PDB struct extraction**: `llvm-pdbutil` (LLVM 22.1.1) at `C:\Program Files\LLVM\bin\`
  ```bash
  llvm-pdbutil dump -types "Z:/ServerSource/WorldManager/WorldManager.pdb"
  ```
- **Binary string extraction**: `dotnet-script` (already set up)
- **Handler scanning**: dotnet-script PE parser (scans for push/push/push registration pattern)
- **Source PDB files**: `Z:/ServerSource/{Login,WorldManager,Zone00,Account,AccountLog}/*.pdb`

---

## Bulk Extraction Tasks

### 1. Full Department + Opcode Enum Extraction
- [ ] Run `llvm-pdbutil dump -types` on WorldManager.pdb (largest, has most departments)
- [ ] Parse all `LF_ENUM` records matching `PROTOCOL_COMMAND_*` or `NC_*`
- [ ] Extract enum member name → numeric value mapping
- [ ] Cross-reference with handler registration scan to fill gaps
- [ ] Repeat for Login.pdb, Zone.pdb for department variants
- [ ] Generate `Departments.cs` with all department enums
- [ ] Generate `Opcodes.cs` with per-department opcode enums

### 2. Full Struct Extraction
- [ ] Parse all `LF_STRUCTURE` records matching `PROTO_NC_*` from WorldManager.pdb
- [ ] For each struct, resolve `LF_FIELDLIST` → get field names, types, offsets
- [ ] Map C++ types to C# types (DWORD→uint, WORD→ushort, BYTE→byte, Name5→char[20], etc.)
- [ ] Generate C# record per struct with binary serialization
- [ ] Repeat for Login.pdb and Zone.pdb for any structs unique to those
- [ ] Total expected: 500+ PROTO structs across all departments

### 3. Handler Address Documentation
- [ ] For each EXE: dump handler registration table (dept, cmd, VA)
- [ ] Cross-reference with PDB function names
- [ ] Write to `docs/handler-addresses.md`:
  ```
  ## WorldManager.exe
  | Address    | Function                              | Dep | Cmd | Notes |
  |------------|---------------------------------------|-----|-----|-------|
  | 0x004251A0 | fc_NC_OPTOOL_CONNECT_BRIF_REQ         | 10  | 1   |       |
  ```
- [ ] Enables future IDA/Ghidra analysis for emulator implementation

### 4. Code Generator
- [ ] Input: extracted enum values + struct definitions (from llvm-pdbutil output)
- [ ] Output: C# source files for FiestaProtocol library
- [ ] Should be re-runnable (regenerate from PDB data, not hand-maintained)
- [ ] Consider storing intermediate extraction as JSON for portability

---

## Open Questions

- [ ] What is the exact cmd number for NC_ACT_NOTICE_REQ in dep 9? (WM has cmd 74 and 88 for ACT)
- [ ] What does the PROTO_NC_ACT_NOTICE_REQ struct look like? (just a string? fixed-length?)
- [ ] What is the SERVER_ID_OPTOOL enum value for the S2S handshake identification?
- [ ] What key value does the S2S handshake expect? (authentication)
- [ ] Are there any community OpTool source references we can cross-reference?
  - Canic's REUPLOAD source (if findable)
  - Mini OpTool (community rebuild)
  - FiestaHeroes documentation
- [ ] What C++ types map to what sizes? (Name4=char[16]? Name5=char[20]? Need to verify from PDB)

---

## Reference

- Full binary analysis: `C:\Projects\Mimir\optool-protocol-analysis.md`
- Server binaries: `Z:\ServerSource\`
- PDB files: `Z:\ServerSource\{Login,WorldManager,Zone00,Account,AccountLog}\*.pdb`
- FiestaHeroes Documentation: https://github.com/FiestaHeroes/Documentation
- LLVM PDB tool: `C:\Program Files\LLVM\bin\llvm-pdbutil.exe`
