English | [中文](README.md)

# MultiPlayer — Juno: New Origins Multiplayer Mod

A Unity mod that adds multiplayer to [Juno: New Origins (JNO)](https://store.steampowered.com/app/870200/): several players enter the same scene, each flies their own craft, and everyone **sees each other's craft in real time**. Loaded via the official ModTools, with FishNet over Steam P2P for networking (public internet, zero port forwarding).

> **Current version 1.51 · prototype stage**: currently "one craft per player, synced to each other as ghost craft"; full multiplayer (multiple craft / fuel / collisions) is still under development. Development and decision records live in [`plans/README.md`](plans/README.md).

---

## What works / what doesn't yet

**Implemented**

- Host + multiple players in the same scene; all data relayed through the host
- Real-time sync of the other player's craft: position, velocity, orientation, part toggles, engine flames, throttle/brake/sliders/translate, activation groups, staging, per-part poses
- High-latency smoothing and continuous extrapolation (teleport threshold, latency EMA, etc.), buffered under network jitter
- **Vizzy isolation**: your visual scripts act only on your own craft — they can't read or control anyone else's, and other players' scripts don't run here
- Steam lobby: hosting is visible, join from the list, Steam friend invites (no port forwarding)
- TCP direct connection for Windows / LAN / VM debugging

**Not yet (known limitations)**

- Currently **one craft per player** (no multiple craft)
- **No sync** of fuel / resources / part damage
- **Pause / time-scale is not followed** — both sides must be at 1× realtime
- No two-way collision physics (kinematic visuals only)
- Requires **identical game version** + **same planet system** (as chosen by the host)

---

## Installation

> 1. Install Juno Harmony
> 2. Put `MultiPlayer.sr2-mod` into the corresponding folder and enable it

---

## Quick start

Once the mod loads, a **MultiPlayer panel** appears in the game UI (the MP button on the toolbar reopens it anytime). The panel shows connection status and player count at the top, grouped by function below.

### Option 1: Steam lobby (recommended, works on public internet)

**Host**

1. Click the "Host" button in the panel → enter a room name
2. The room is **public**; once created, a dialog shows the room name + your SteamId
3. Wait for friends to join, or click "Invite friends" to send Steam invites directly

**Join**

1. Click "Room list" → "Refresh"
2. Click the target room's "Join" in the list; or just accept a Steam invite

**Disconnect**: click "Disconnect" (host = close the room, client = leave).

### Option 2: LAN / VM debugging (TCP)

- Host: Debug group → "TCP host", default port `25555`
- Joiner: Debug group → "TCP join", enter the host address (format: `hostIP[:port]`)

> TCP requires the host to open the corresponding firewall port; the Steam path needs no port settings at all.

### Option 3: Developer console commands (DevConsole)

Type these in the in-game DevConsole:

| Command | Args | Purpose |
|---|---|---|
| `SteamLobbyCreate` | room name | Create a public Steam room (recommended) |
| `SteamLobbyList` | — | List rooms |
| `SteamLobbyListWorld` | — | List rooms (cross-region) |
| `SteamLobbyJoin` | lobby id | Join by lobby id |
| `SteamLobbyLeave` | — | Leave / close the room |
| `SteamHostLobby` | port | Legacy: host a Steam room by port |
| `SteamJoinLobby` | host SteamId | Legacy: join by the host's SteamId |
| `TcpHostLobby` | port | TCP host (debugging) |
| `TcpJoinLobby` | address port | TCP join (debugging) |
| `StopLobby` | — | Stop the session |
| `SetTickRate` | Hz (default 20) | Set the state send rate |

Network simulation for debugging (`NetSimOn/Off/Reset`, `NetSimDelay/Jitter/Loss/Duplicate`): injects deliberate latency / jitter / loss / duplicates locally to reproduce or self-test network behavior — not needed for normal play.

---

## Before you play

- Both sides use the **same game version**, and the same mod version
- Both enter the **same planet system** (as chosen by the host), with both loaded into the flight scene
- Both at **1× realtime** (no pause, no time-scale)
- One craft per player for now

---

## FAQ

- **The other player's craft sometimes "stutters / jumps"**: extrapolation and smoothing under high latency, expected; you can reproduce it with `NetSimDelay/Jitter`. The stop–surge stutter is being actively improved.
- **Hosting / joining does nothing**: make sure both sides are online on Steam and the game is connected to Steam (Steam path), or that the port is reachable (TCP path).
- **The other player's Vizzy input dialogs etc. leak over here**: Vizzy is isolated in multiplayer; if it still reproduces, send the author the `VizzyIsolation` lines from `Player.log`.

---

## Development / building

This repository is the mod's full source. Analysis, architecture, decisions and task lists are maintained in [`plans/README.md`](plans/README.md) (internal docs, Git-managed; values involving private local paths live only locally and never enter the repo).

- Build: `dotnet build MultiPlayer.csproj` (0 errors / 0 warnings)
- Dependencies: official ModTools, FishNet.Runtime / GameKit.Dependencies (see the `.csproj` files)

## License

[MIT](LICENSE) · © 2026 Maeriberry Hearn