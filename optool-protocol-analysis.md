# OpTool & S2S Protocol Analysis

Binary analysis of Fiesta Online server executables from `Z:/ServerSource/`.
Source code was built at `E:\ProjectF2\` (PDB paths confirm).

---

## 1. Server Process Inventory

| Process       | EXE             | PDB  | OpTool Port | Notes                     |
|---------------|-----------------|------|-------------|---------------------------|
| Login         | Login.exe       | Yes  | 9012        | Client gateway            |
| WorldManager  | WorldManager.exe| Yes  | 9015        | Central coordinator       |
| Zone00-04     | Zone.exe        | Yes  | 9018,21,24,27,30 | +3 per zone        |
| Account       | Account.exe     | Yes  | -           | DB bridge (Account DB)    |
| AccountLog    | AccountLog.exe  | Yes  | -           | DB bridge (AccountLog DB) |
| Character     | Character.exe   | No   | -           | DB bridge (Character DB)  |
| GameLog       | GameLog.exe     | No   | -           | DB bridge (GameLog DB)    |
| GamigoZR      | GamigoZR.exe    | No   | -           | HTTP service (GR.php)     |

OpTool ports from ServerInfo.txt type 8 entries.

---

## 2. Protocol Departments (from handler registration scanning)

| Dep | Hex  | Name          | Login | WM  | Zone | Account |
|-----|------|---------------|-------|-----|------|---------|
| 2   | 0x02 | MISC(login)   | 9     | 38  | 21   | 10      |
| 3   | 0x03 | AVATAR        | 24    | 14  | 5    | 13      |
| 4   | 0x04 | CHAR_DB       | -     | 32  | 32   | -       |
| 5   | 0x05 | CHAR          | -     | 10  | -    | 2       |
| 6   | 0x06 | ITEM          | -     | 4   | 10   | -       |
| 7   | 0x07 | MAP           | -     | -   | 1    | -       |
| 8   | 0x08 | MISC          | -     | 4   | 35   | -       |
| 9   | 0x09 | ACT           | -     | 2   | 9    | -       |
| **10** | **0x0A** | **OPTOOL** | **8** | **22** | **1** | **4** |
| 12  | 0x0C | ITEM_DB       | -     | -   | 49   | -       |
| 13  | 0x0D | MOB/NPC       | -     | 1   | 68   | -       |
| 14  | 0x0E | GUILD         | -     | 14  | 12   | -       |
| 15  | 0x0F | BRIEFINFO     | -     | -   | 1    | -       |
| 16  | 0x10 | PARTY         | 1     | 22  | 12   | 1       |
| 17  | 0x11 | TRADE         | -     | -   | 15   | -       |
| 18  | 0x12 | BOOTH         | -     | 1   | 10   | -       |
| 19  | 0x13 | QUEST         | -     | -   | 10   | -       |
| 20  | 0x14 | KQ            | -     | 2   | 5    | -       |
| 21  | 0x15 | FRIEND        | -     | 12  | 1    | -       |
| 22  | 0x16 | GUILD2        | -     | 13  | 8    | -       |
| 23  | 0x17 | MINIHOUSE     | -     | -   | 5    | -       |
| 24  | 0x18 | AUCTION       | -     | 1   | 5    | -       |
| 26  | 0x1A | INSTANCE_DGN  | -     | 1   | 8    | -       |
| 27  | 0x1B | DICE/TAISAI   | -     | -   | 2    | -       |
| 28  | 0x1C | CHARGED       | -     | 37  | -    | -       |
| 29  | 0x1D | CHATRESTRICT  | -     | 43  | 8    | -       |
| 31  | 0x1F | HOLYP/EVENT   | -     | 8   | 2    | -       |
| 32  | 0x20 | GAMBLE        | -     | 7   | 1    | -       |
| 35  | 0x23 | PET           | -     | -   | 27   | -       |
| 36  | 0x24 | CHARTITLE     | -     | 3   | 7    | -       |
| 37  | 0x25 | GUILDACADEMY  | -     | 14  | 7    | -       |
| 38  | 0x26 | COLLECT       | -     | 44  | 12   | -       |

---

## 3. OPTOOL Department (0x0A) Handler Registrations

### Login.exe — 8 OPTOOL handlers
| Cmd | Hex  | Handler    | Function (from PDB)                              |
|-----|------|------------|--------------------------------------------------|
| 1   | 0x01 | 0x004074B0 | fc_NC_OPTOOL_CONNECT_BRIF_REQ                    |
| 3   | 0x03 | 0x00407980 | fc_NC_OPTOOL_MAP_USER_LIST_REQ                   |
| 5   | 0x05 | 0x00407DD0 | fc_NC_OPTOOL_S2SCONNECT_LIST_REQ                 |
| 7   | 0x07 | 0x00407A30 | fc_NC_OPTOOL_CLOSE_SERVER_REQ                    |
| 18  | 0x12 | 0x00407C90 | fc_NC_OPTOOL_LOGIN_USER_RATABLE_GET_REQ          |
| 20  | 0x14 | 0x00407D50 | fc_NC_OPTOOL_LOGIN_USER_RATABLE_SET_CMD          |
| 23  | 0x17 | 0x00407DD0 | fc_NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_REQ         |
| 24  | 0x18 | 0x00407DF0 | fc_NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_CLR         |

### WorldManager.exe — 22 OPTOOL handlers (most feature-rich)
| Cmd | Hex  | Handler    | Function (from PDB)                              |
|-----|------|------------|--------------------------------------------------|
| 1   | 0x01 | 0x004251A0 | fc_NC_OPTOOL_CONNECT_BRIF_REQ                    |
| 3   | 0x03 | 0x00425860 | fc_NC_OPTOOL_MAP_USER_LIST_REQ                   |
| 5   | 0x05 | 0x00425930 | fc_NC_OPTOOL_S2SCONNECT_LIST_REQ                 |
| 7   | 0x07 | 0x00425950 | fc_NC_OPTOOL_CLOSE_SERVER_REQ                    |
| 9   | 0x09 | 0x00427790 | fc_NC_OPTOOL_KICK_USER_REQ                       |
| 11  | 0x0B | 0x00425D30 | fc_NC_OPTOOL_FIND_USER_REQ                       |
| 12  | 0x0C | 0x00425E20 | fc_NC_OPTOOL_CHARACTER_DELETE_REQ                |
| 14  | 0x0E | 0x00426390 | fc_NC_OPTOOL_GUILD_DATA_CHANGE_REQ               |
| 15  | 0x0F | 0x004263F0 | fc_NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_REQ       |
| 17  | 0x11 | 0x00425D70 | fc_NC_OPTOOL_CHARACTER_DELETE_CMD                 |
| 21  | 0x15 | 0x004264D0 | fc_NC_OPTOOL_GUILD_DISMISS_CANCEL_REQ            |
| 26  | 0x1A | 0x00414E40 | fc_NC_OPTOOL_REQ_CLIENT_NUM_OF_USER_LIMIT        |
| 27  | 0x1B | 0x004265A0 | fc_NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT        |
| 29  | 0x1D | 0x00426660 | fc_NC_OPTOOL_WM_SEND_PACKET_STATISTICS_REQ       |
| 31  | 0x1F | 0x00426730 | fc_NC_OPTOOL_WM_SEND_PACKET_STATISTICS_CLR       |
| 32  | 0x20 | 0x00427630 | fc_NC_OPTOOL_KQ_SCHEDULE_REQ                     |
| 34  | 0x22 | 0x00426990 | fc_NC_OPTOOL_KQ_CHANGE_CMD                       |
| 36  | 0x24 | 0x00427980 | fc_NC_OPTOOL_KQ_DELETE_CMD                       |
| 38  | 0x26 | 0x00426C10 | fc_NC_OPTOOL_KQ_ALL_RESET_CMD                    |
| 39  | 0x27 | 0x00426C50 | fc_NC_OPTOOL_KQ_MAP_ALLOC_INFO_REQ               |
| 41  | 0x29 | 0x00426E20 | fc_NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_REQ |
| 43  | 0x2B | 0x00427030 | fc_NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_CMD         |

### Zone.exe — 1 OPTOOL handler
| Cmd | Hex  | Handler    | Function (from PDB)                              |
|-----|------|------------|--------------------------------------------------|
| 7   | 0x07 | 0x004C82B0 | fc_NC_OPTOOL_CONNECT_BRIF_REQ (response only)    |

### Account.exe — 4 OPTOOL handlers
| Cmd | Hex  | Handler    | Function (from PDB)                              |
|-----|------|------------|--------------------------------------------------|
| 1   | 0x01 | 0x00404DD0 | fc_NC_OPTOOL_CONNECT_BRIF_REQ                    |
| 3   | 0x03 | 0x00404FC0 | fc_NC_OPTOOL_MAP_USER_LIST_REQ                   |
| 5   | 0x05 | 0x00405070 | fc_NC_OPTOOL_S2SCONNECT_LIST_REQ                 |
| 7   | 0x07 | 0x00405140 | fc_NC_OPTOOL_CLOSE_SERVER_REQ                    |

---

## 4. NC_OPTOOL Opcodes — Complete Enum (52 unique, from all PDBs)

| Opcode Name                                   | Present In                               |
|-----------------------------------------------|------------------------------------------|
| NC_OPTOOL_NULL                                | All                                      |
| NC_OPTOOL_CONNECT_BRIF_REQ                    | All                                      |
| NC_OPTOOL_CONNECT_BRIF_ACK                    | All                                      |
| NC_OPTOOL_MAP_USER_LIST_REQ                   | All                                      |
| NC_OPTOOL_MAP_USER_LIST_ACK                   | All                                      |
| NC_OPTOOL_MAP_USER_LIST_INFO                  | Account, AccountLog                      |
| NC_OPTOOL_S2SCONNECT_LIST_REQ                 | All                                      |
| NC_OPTOOL_S2SCONNECT_LIST_ACK                 | All                                      |
| NC_OPTOOL_CLOSE_SERVER_REQ                    | All                                      |
| NC_OPTOOL_CLOSE_SERVER_ACK                    | All                                      |
| NC_OPTOOL_KICK_USER_REQ                       | All                                      |
| NC_OPTOOL_KICK_USER_ACK                       | All                                      |
| NC_OPTOOL_FIND_USER_REQ                       | All                                      |
| NC_OPTOOL_FIND_USER_ACK                       | All                                      |
| NC_OPTOOL_CHARACTER_DELETE_REQ                | All                                      |
| NC_OPTOOL_CHARACTER_DELETE_ACK                | All                                      |
| NC_OPTOOL_CHARACTER_DELETE_CMD                | All                                      |
| NC_OPTOOL_GUILD_DATA_CHANGE_REQ              | All                                      |
| NC_OPTOOL_GUILD_DATA_CHANGE_ACK              | All                                      |
| NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_REQ      | All                                      |
| NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_ACK      | All                                      |
| NC_OPTOOL_GUILD_DISMISS_CANCEL_REQ           | All                                      |
| NC_OPTOOL_GUILD_DISMISS_CANCEL_ACK           | All                                      |
| NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_CMD        | All                                      |
| NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_ACK        | All                                      |
| NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_REQ| All                                      |
| NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_ACK| All                                      |
| NC_OPTOOL_REQ_CLIENT_NUM_OF_USER_LIMIT       | All                                      |
| NC_OPTOOL_ACK_CLIENT_NUM_OF_USER_LIMIT       | All                                      |
| NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT       | All                                      |
| NC_OPTOOL_LOGIN_USER_RATABLE_GET_REQ         | All                                      |
| NC_OPTOOL_LOGIN_USER_RATABLE_GET_ACK         | All                                      |
| NC_OPTOOL_LOGIN_USER_RATABLE_SET_CMD         | All                                      |
| NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_REQ        | All                                      |
| NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_ACK        | All                                      |
| NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_CLR        | All                                      |
| NC_OPTOOL_WM_SEND_PACKET_STATISTICS_REQ      | All                                      |
| NC_OPTOOL_WM_SEND_PACKET_STATISTICS_ACK      | All                                      |
| NC_OPTOOL_WM_SEND_PACKET_STATISTICS_CLR      | All                                      |
| NC_OPTOOL_KQ_SCHEDULE_REQ                    | All                                      |
| NC_OPTOOL_KQ_SCHEDULE_ACK                    | All                                      |
| NC_OPTOOL_KQ_CHANGE_CMD                      | All                                      |
| NC_OPTOOL_KQ_DELETE_CMD                      | All                                      |
| NC_OPTOOL_KQ_ALL_RESET_CMD                   | All                                      |
| NC_OPTOOL_KQ_MAP_ALLOC_INFO_REQ              | All                                      |
| NC_OPTOOL_KQ_MAP_ALLOC_INFO_ACK              | All                                      |
| NC_OPTOOL_CLOSE                               | WorldManager                             |
| NC_OPTOOL_CO                                  | Zone                                     |
| NC_OPTOOL_KQ_DELETE_                          | WorldManager                             |

### Opcode-to-Cmd Mapping (from handler registration)

Based on handler scanning of WorldManager.exe (most complete):
| Cmd | Opcode                                        |
|-----|-----------------------------------------------|
| 0   | NC_OPTOOL_NULL                                |
| 1   | NC_OPTOOL_CONNECT_BRIF_REQ                    |
| 2   | NC_OPTOOL_CONNECT_BRIF_ACK                    |
| 3   | NC_OPTOOL_MAP_USER_LIST_REQ                   |
| 4   | NC_OPTOOL_MAP_USER_LIST_ACK                   |
| 5   | NC_OPTOOL_S2SCONNECT_LIST_REQ                 |
| 6   | NC_OPTOOL_S2SCONNECT_LIST_ACK                 |
| 7   | NC_OPTOOL_CLOSE_SERVER_REQ                    |
| 8   | NC_OPTOOL_CLOSE_SERVER_ACK                    |
| 9   | NC_OPTOOL_KICK_USER_REQ                       |
| 10  | NC_OPTOOL_KICK_USER_ACK                       |
| 11  | NC_OPTOOL_FIND_USER_REQ                       |
| 12  | NC_OPTOOL_CHARACTER_DELETE_REQ                |
| 13  | NC_OPTOOL_CHARACTER_DELETE_ACK                |
| 14  | NC_OPTOOL_GUILD_DATA_CHANGE_REQ              |
| 15  | NC_OPTOOL_GUILD_CHANGE_MEMBER_GRADE_REQ      |
| 17  | NC_OPTOOL_CHARACTER_DELETE_CMD                |
| 18  | NC_OPTOOL_LOGIN_USER_RATABLE_GET_REQ          |
| 19  | NC_OPTOOL_LOGIN_USER_RATABLE_GET_ACK          |
| 20  | NC_OPTOOL_LOGIN_USER_RATABLE_SET_CMD          |
| 21  | NC_OPTOOL_GUILD_DISMISS_CANCEL_REQ            |
| 23  | NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_REQ         |
| 24  | NC_OPTOOL_LOGON_PROCESS_TIME_VIEW_CLR         |
| 26  | NC_OPTOOL_REQ_CLIENT_NUM_OF_USER_LIMIT        |
| 27  | NC_OPTOOL_SET_CLIENT_NUM_OF_USER_LIMIT        |
| 29  | NC_OPTOOL_WM_SEND_PACKET_STATISTICS_REQ       |
| 31  | NC_OPTOOL_WM_SEND_PACKET_STATISTICS_CLR       |
| 32  | NC_OPTOOL_KQ_SCHEDULE_REQ                     |
| 34  | NC_OPTOOL_KQ_CHANGE_CMD                       |
| 36  | NC_OPTOOL_KQ_DELETE_CMD                       |
| 38  | NC_OPTOOL_KQ_ALL_RESET_CMD                    |
| 39  | NC_OPTOOL_KQ_MAP_ALLOC_INFO_REQ               |
| 41  | NC_OPTOOL_GUILD_TOURNAMENT_SCHEDULE_RESET_REQ |
| 43  | NC_OPTOOL_GUILD_TOURNAMENT_CHANGE_CMD         |

---

## 5. Server Announcements / GM Say

**NC_ACT_NOTICE_REQ is the "GM Say" / server announcement command.**

It is NOT an OPTOOL-department opcode. It lives in department 9 (ACT).
But it is handled by the OpTool parser in WorldManager:

```
CParserOPTool::fc_NC_ACT_NOTICE_REQ     (OpTool → WorldManager)
CParserClient::fc_NC_ACT_NOTICE_REQ     (GameClient → WorldManager)
CParserZone::fc_NC_ACT_NOTICE_REQ       (Zone → WorldManager)
```

Flow: OpTool sends NC_ACT_NOTICE_REQ to WM's OpTool port (9015).
WM broadcasts NC_ACT_NOTICE_CMD to all connected clients.

### WorldManager OpTool parser also handles non-OPTOOL packets:
- `fc_NC_ACT_NOTICE_REQ` (dep 9, ACT — server announcements)
- `fc_NC_EVENT_ADD_EVENT_REQ` (event management)
- `fc_NC_EVENT_DEL_EVENT_REQ`
- `fc_NC_EVENT_GET_ALL_EVENT_INFO_REQ`
- `fc_NC_EVENT_SET_ALL_READY_REQ`
- `fc_NC_EVENT_UPDATE_EVENT_REQ`
- `fc_NC_HOLY_PROMISE_DB_DEL_CHAR_ACK`
- `fc_NC_MISC_PINGTEST_TOOL_WM_*` (4 ping test variants)
- `fc_NC_MISC_XTRAP2_OPTOOL_READ_CODEMAP_REQ` (anti-cheat)
- `fc_NC_PARTY_MEMBERINFORM_REQ`

### Related protocol structs:
- `PROTO_NC_ACT_NOTICE_REQ` — request from OpTool/client/zone
- `PROTO_NC_ACT_NOTICE_CMD` — broadcast command to clients
- `PROTO_NC_ACT_NOTICE_CMD_SEND` — internal WM send wrapper
- `PROTO_NC_ANNOUNCE_Z2W_CMD` — zone-to-WM announcement
- `PROTO_NC_ANNOUNCE_W2C_CMD` — WM-to-client announcement

### Announce system (Zone-side):
- `CAnnounceSystem` — reads `AnnounceData.shn` and `MobKillAnnounce.shn`
- Triggers: LevelMax, ClassUp, ItemTake, ItemUpgrade, CharacterTitle, ProposeAccept, WeddingStart, Roar
- Sends via `Send_PROTO_NC_ANNOUNCE_Z2W_CMD` to WorldManager

### Zone ampersand commands:
- `AmpersandCommand::ac_NoticeZone` — zone-local notice
- `ac_NoticeWorld` — world-wide notice (referenced in Zone PDB)

---

## 6. S2S Communication Protocol

### Packet Format (same for client↔server and server↔server)
```
[Length] [Opcode: 2 bytes] [Payload]

Length encoding:
  1-254:   1 byte (value = length of opcode+payload)
  255+:    0x00 + 2 bytes big-endian length

Opcode: 2 bytes
  byte 0 = department (0x08=MISC, 0x0A=OPTOOL, 0x09=ACT, etc.)
  byte 1 = command within department
```

S2S traffic is **unencrypted** (no XOR/seed exchange).

### S2S Handshake (MISC department = 0x08)
```
Server listens → accepts connection
Server sends:  NC_MISC_S2SCONNECTION_RDY   (0x0801)
Client sends:  NC_MISC_S2SCONNECTION_REQ   (0x0802) + identification
Server sends:  NC_MISC_S2SCONNECTION_ACK   (0x0803)
Heartbeat:     NC_MISC_HEARTBEAT_REQ       (0x0808)
```

Identification payload (from string: `NC_MISC_S2SCONNECTION_REQ <server_from id=%d w=%d z=%d, server_id_to=%d, key=%d, my_type=%d>`):
- server_from id, world, zone
- server_id_to
- key (authentication)
- my_type (server type enum)

### Server Types (SERVER_ID enum):
- `SERVER_ID_OPTOOL` — OpTool connection type

### Session Classes per process:

**Login:**
- `CLoginOPToolSession` / `CLoginOPToolSessionManager`
- `CParserOPTool` with `CLoginOPToolSession` parameter
- `LoginServer::Listen_OPTool()`

**WorldManager:**
- `CWMOPToolSession` / `CWMOPToolSessionManager`
- `CParserOPTool` with `CWMOPToolSession` parameter
- `WorldManagerServer::Listen_OPTool()`
- Also: `CParserClient`, `CParserZone`, `CParserCharDB`

**Zone:**
- `OPToolSession` (simpler, standalone class)
- `OPToolObject` / `OPToolList`
- Uses `NETCOMMAND*` instead of `NETPACKET*` (different session model)
- Only handles `opts_NC_OPTOOL_CONNECT_BRIF_REQ`

---

## 7. Source Code Structure (from PDB paths)

```
E:\ProjectF2\
  CSCode\                              (shared code)
    Protocol\
      protocol.h                       (master protocol header)
      protooptool.h                    (OpTool packet structs)
      protoact.h                       (NC_ACT_* packets including NOTICE)
      protoannounce.h                  (NC_ANNOUNCE_* packets)
      protomisc.h                      (NC_MISC_* S2S packets)
      proto*.h                         (30+ protocol headers)

  Server\
    3LoginServer2\
      Source\Protocol\
        pf_optool.cpp / .h             (OpTool packet handlers)
      Source\Sessions\
        loginoptoolsession.cpp / .h
        loginoptoolsessionmanager.cpp / .h

    4WorldManagerServer2\
      Source\Protocol\
        pf_optool.cpp / .h             (OpTool packet handlers — most extensive)
      Source\Sessions\
        wmoptoolsession.cpp / .h
        wmoptoolsessionmanager.cpp / .h

    5ZoneServer2\
      optoolsession.cpp / .h           (simpler, at root level)
      pf_optool.cpp                    (minimal handler set)
      AnnounceSystem.cpp               (CAnnounceSystem)
```

---

## 8. GamigoZR HTTP Protocol

Zone servers call GamigoZR on startup:
```
GET /GR.php?act=boot&title=Fiesta&nation=EU_US_REAL&pw=wkdbdmldutlstkd&world=0&machine=Zone4
```
- `pw=wkdbdmldutlstkd` — hardcoded authentication password
- `machine=ZoneN` — identifies which zone is booting
- GamigoZR returns same response for all requests

---

## 9. OpTool Connection Flow (to send a server announcement)

1. Connect TCP to WorldManager OpTool port (default 9015)
2. Receive `NC_MISC_S2SCONNECTION_RDY` (0x0801)
3. Send `NC_MISC_S2SCONNECTION_REQ` (0x0802) with SERVER_ID_OPTOOL identification
4. Receive `NC_MISC_S2SCONNECTION_ACK` (0x0803)
5. Send `NC_ACT_NOTICE_REQ` (dep=0x09, cmd=?) with message text
6. WM broadcasts `NC_ACT_NOTICE_CMD` to all connected game clients
7. Maintain connection with `NC_MISC_HEARTBEAT_REQ` (0x0808) / ACK

### ACT department (0x09) handlers in WorldManager:
| Cmd | Hex  | Likely Opcode           |
|-----|------|-------------------------|
| 74  | 0x4A | NC_ACT_NOTICE_REQ (?)   |
| 88  | 0x58 | NC_ACT_SCRIPT_MSG_WORLD_CMD (?) |

Note: Exact cmd numbers for NC_ACT_NOTICE_REQ need verification via packet capture or deeper disassembly. The WM has only 2 ACT handlers registered.

---

## 10. OperatorTool Database

ODBC_INFO index 3 in ServerInfo.txt = OperatorTool database.
- `CSQLPAccount::SQL_USER_CREATE_ACC_OPTOOL` — creates OpTool user accounts
- `usp_Optool_GetCharacterTitleAll` — stored proc for character title queries
- `NC_MISC_XTRAP2_OPTOOL_READ_CODEMAP_REQ` — XTrap anti-cheat codemap reading

---

## 11. Key Observations

1. **WorldManager is the OpTool hub** — has 22 OPTOOL handlers + handles cross-department packets (ACT, EVENT, MISC) from OpTool sessions
2. **Login has 8 OPTOOL handlers** — focused on connection info, user limits, logon statistics
3. **Zone has only 1 OPTOOL handler** — just responds to CONNECT_BRIF_REQ
4. **Account/AccountLog have 4 each** — basic monitoring (connect, map users, s2s list, close)
5. **Character/GameLog/GamigoZR have 0** — no OpTool protocol, only S2S MISC
6. **Server announcements go through WorldManager** via NC_ACT_NOTICE_REQ, not through the OPTOOL department
7. **All servers share the same protocol enum** (protooptool.h in CSCode) but only implement handlers for their relevant subset
