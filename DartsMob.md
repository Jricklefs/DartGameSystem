# DartsMob Development Notes

## Performance Improvements (TODO)

### TensorRT Migration
**Priority: High** | **Impact: 2-3x faster inference, no idle timeout**

Currently using OpenVINO for YOLO inference. OpenVINO has an idle timeout (~5 seconds) that causes cold start latency spikes (500ms+ for first dart after pause).

**Current workaround:** Background warmup thread runs dummy inference every 3 seconds.

**Better solution:** Export models to TensorRT format for native NVIDIA GPU execution.

Benefits:
- Native CUDA execution on RTX 4090
- No idle/sleep issues - GPU memory stays allocated
- 2-3x faster inference (~50-80ms vs 150-200ms)
- Lower latency variance

Steps to migrate:
```bash
# Export YOLO model to TensorRT (one-time, takes 5-10 min)
yolo export model=posenano27122025.pt format=engine device=0

# Creates .engine file optimized for RTX 4090
```

Then update `detection.py` to load `.engine` instead of `_openvino_model`.

Requirements:
- CUDA + cuDNN (likely already installed)
- TensorRT (install via NVIDIA)
- The .engine file is GPU-specific (won't work on other machines)

### OpenVINO Caching (Implemented)
Set `OPENVINO_CACHE_DIR` to cache compiled models. Reduces cold start from ~500ms to ~100ms.

```python
os.environ['OPENVINO_CACHE_DIR'] = r'C:\Users\clawd\openvino_cache'
```

---

## Known Issues

### Dart 4 False Positives
- **Cause:** Hand pullback detected as new dart before clearing mode activates
- **Fix (implemented):** Auto-enter clearing mode when motion detected after 3 darts

### X01 Checkout Bug  
- Reports of valid double-out being treated as bust
- Added detailed logging to diagnose
- Status: Under investigation

---

## Architecture Notes

### Detection Pipeline
```
DartSensor (motion) → DartDetect (YOLO + scoring) → DartGame API (game logic)
     ↓                        ↓                            ↓
  Port 8001              Port 8000                    Port 5000
```

### Key Thresholds (DartSensor)
- Base: 5.0% - minimum change to detect anything
- Dart: 4.0% - change threshold for new dart
- Clear: 3.0% - threshold to confirm board cleared
- Clearing: 200.0% - threshold for hand/clearing motion
