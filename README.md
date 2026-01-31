# DartGameSystem

Your dart game implementation using DartDetect API.

## Components

### DartGameAPI (.NET 8)
- Game state management
- User accounts and authentication
- Game modes (501, Cricket, etc.)
- Score tracking and history
- WebSocket for real-time updates to Roku

### Architecture

```
┌─────────────┐     motion      ┌─────────────────────────────────────┐
│ DartSensor  │ ───────────────►│          DartGameAPI                │
│ (cameras)   │                 │                                     │
└─────────────┘                 │  ┌─────────────┐  ┌──────────────┐ │
                                │  │ Game State  │  │ DartDetect   │ │
                                │  │ (in-memory) │  │ API Client   │ │
                                │  └─────────────┘  └──────────────┘ │
                                │         │                │         │
                                │         ▼                ▼         │
                                │  ┌─────────────┐  ┌──────────────┐ │
                                │  │ WebSocket   │  │ HTTP calls   │ │
                                │  │ (to Roku)   │  │ to :8000     │ │
                                │  └─────────────┘  └──────────────┘ │
                                └─────────────────────────────────────┘
```

## Configuration

```json
{
  "DartDetectApi": {
    "BaseUrl": "http://localhost:8000",
    "ApiKey": "your-api-key"
  },
  "Boards": {
    "joes-board": {
      "Cameras": ["cam0", "cam1", "cam2"]
    }
  }
}
```

## Running

```bash
cd DartGameAPI
dotnet run
```

API runs on http://localhost:5000

## Endpoints

### Boards
- `GET /api/boards` - List boards
- `POST /api/boards/{id}/calibrate` - Trigger calibration

### Games
- `POST /api/games` - Start new game
- `GET /api/games/{id}` - Get game state
- `POST /api/games/{id}/throw` - Process dart throw
- `DELETE /api/games/{id}` - End game

### WebSocket
- `ws://localhost:5000/ws/{boardId}` - Real-time game updates
