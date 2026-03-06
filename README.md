# DartGameSystem

The central hub for the DartsMob automated dart scoring system.

## Architecture

```
┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────┐
│  DartSensorDirect   │────>│    DartGameSystem     │     │  DartDetectLib  │
│  (Edge Device)      │     │    (Game Server)      │     │  (C++ Source)   │
│                     │     │                       │     │                 │
│  - Camera capture   │     │  - Game logic (.NET)  │     │  - Detection    │
│  - Motion detection │     │  - SignalR hub        │     │  - Scoring      │
│  - DLL detection    │     │  - Web UI (JS/CSS)    │     │  - Triangulation│
│  - Local DLL copy   │     │  - Local DLL copy     │     │                 │
└─────────────────────┘     └───────────────────────┘     └─────────────────┘
        │                             │                           │
        │  Sends scores via           │  Uses DLL via             │  Source of truth
        │  DirectResult (HTTP)        │  NativeDartDetectService  │  for all C++ code
        │  + SignalR events           │  (P/Invoke)               │
        └─────────────────────────────┘                           │
                                      │                           │
                                      └───── Built DLL copied ───┘
                                             from here
```

## Repos

| Repo | Purpose | GitHub |
|------|---------|--------|
| **DartGameSystem** | .NET game API, SignalR hub, web UI | `Jricklefs/DartGameSystem` |
| **DartSensorDirect** | Python edge pipeline (cameras + DLL + motion) | `Jricklefs/DartSensorDirect` |
| **DartDetectLib** | C++ DLL source (detection, scoring, triangulation) | `Jricklefs/DartDetectLib` |
| **DartSensor** | Original motion sensor (legacy, backed up) | `Jricklefs/DartSensor` |
| **DartDetectionAI** | Python AI detection (YOLO calibration module) | `Jricklefs/DartDetectionAI` |

## DLL Management

**DartDetectLib** is the single source of truth for the C++ detection DLL.

### Workflow for DLL changes:
1. Edit C++ code in `DartDetectLib` repo
2. Build: `cd DartDetectLib && build.bat`
3. Copy built DLL to:
   - `DartGameSystem/DartDetectLib/build/bin/Release/DartDetectLib.dll`
   - `DartSensorDirect/lib/DartDetectLib.dll`
4. Commit the updated DLL binary in both repos

### Why copies instead of references?
- **DartSensorDirect** will eventually run on edge devices (Raspberry Pi) — can't reference a local dev path
- **DartGameSystem** needs the DLL in its build output — simpler to have a local copy
- Each repo is self-contained and deployable independently

## Branches

- **`main`** — stable, production-ready
- **`feature/direct-result`** — DirectResult endpoint, DartSensorDirect integration, UI fixes (active development)

## Key Components

### .NET API (DartGameAPI/)
- **Game logic** — X01, Cricket, Around the Clock, etc.
- **SignalR Hub** (`/gamehub`) — Real-time events (StartGame, DartThrown, PauseDetection, etc.)
- **DirectResult endpoint** — Accepts pre-scored darts from DartSensorDirect (no image transfer)
- **Benchmark API** — Game/dart storage, replay, accuracy tracking
- **NativeDartDetectService** — C++ DLL integration via P/Invoke (fallback when DartSensorDirect isn't used)

### Web UI (wwwroot/)
- **game.html** — Main game interface
- **settings.html** — Calibration, accuracy, system settings
- **Speakeasy theme** — 1920s prohibition-era aesthetic

### SignalR Events
- `StartGame(gameId)` / `StopGame(boardId)` — Game lifecycle
- `DartThrown(dart, game)` — Score updates
- `PauseDetection(reason)` / `ResumeDetection` — Stop/resume sensor during busts/wins
- `RegisterBoard(boardId)` — Sensor registration (must re-register on reconnect)

## Database
- **SQL Server**: `JOESSERVER2019`
- **Database**: `DartsMobDB`
- **Connection**: `Server=JOESSERVER2019;Database=DartsMobDB;User Id=DartsMobApp;Password=Stewart14s!2;TrustServerCertificate=True;`

## Running

```bash
# Build
cd DartGameAPI && dotnet build -c Release

# Run (must specify port)
dotnet run -c Release --urls http://0.0.0.0:5000

# Or use desktop shortcut
# START_DARTSMOB_DIRECT.bat (starts DartGame + DartSensorDirect)
```

## Board ID
Default board: `A3C8DCD1-4196-4BF6-BD20-50310B960745`
