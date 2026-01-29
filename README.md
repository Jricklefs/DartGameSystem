# DartSensor

Lightweight dart detection client for Raspberry Pi. Detects darts via frame differencing and sends snapshots to the DartDetectionAI server for tip detection and scoring.

## Architecture

```
[Cameras] → [DartSensor (Pi)] → [DartDetectionAI Server]
                                        ↓
                                 [Game Client/Roku]
```

DartSensor is a "dumb sensor" - it only detects motion and reports to the server. All game logic, scoring, and calibration live server-side.

## Features

- Multi-camera support (USB or Pi cameras)
- Frame differencing against baseline image
- Configurable detection thresholds
- Headless operation (no display required)
- Lightweight enough for Raspberry Pi 4/5

## Requirements

- Raspberry Pi 4 or 5 (or any Linux box)
- Python 3.9+
- USB cameras or Pi camera module
- Network connection to detection server

## Installation

```bash
# Clone the repo
git clone https://github.com/cbDoingit/DartSensor.git
cd DartSensor

# Install dependencies
pip install -r requirements.txt

# Copy and edit config
cp config/settings.example.yaml config/settings.yaml
nano config/settings.yaml

# Run
python src/main.py
```

## Configuration

Edit `config/settings.yaml`:

```yaml
server:
  url: "https://your-detection-server.com"
  api_key: "your-api-key"  # optional

cameras:
  - id: "board1-cam0"
    device: 0
  - id: "board1-cam1" 
    device: 1

detection:
  diff_threshold: 25        # pixel difference threshold
  min_contour_area: 500     # minimum area to count as dart
  settling_ms: 100          # wait after detection before snapshot
  baseline_match_pct: 98    # how close to baseline = "clear"
```

## API

DartSensor calls these endpoints on your detection server:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/detect` | POST | Send image + camera_id, get tip positions + scores |
| `/events/dart` | POST | Notify server dart was detected |
| `/events/clear` | POST | Notify server board is clear (turn complete) |

## State Machine

```
IDLE → (start game) → WAITING_FOR_DART
WAITING_FOR_DART → (diff detected) → SETTLING
SETTLING → (stable) → CAPTURING → (send to server) → WAITING_FOR_DART or WAITING_FOR_CLEAR
WAITING_FOR_CLEAR → (matches baseline) → TURN_COMPLETE → WAITING_FOR_DART
```

## License

MIT
