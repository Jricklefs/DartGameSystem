"""
DartSensor API
OpenCV-based dart detection with visual debug UI.
Connects to DartGame API via SignalR for game events.
Sends dart detections to DartGame API (the hub).
"""

import sys
import os

# Ensure we can import from same directory
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import cv2
import numpy as np
import time
import logging
import json
import threading
import requests
import base64
from pathlib import Path
from typing import Optional, List, Tuple
from dataclasses import dataclass, asdict, field
from collections import deque

from detector_v2 import MultiCameraDartDetector, DartDetectorV2, DetectorConfig, BoardState

# SignalR client
try:
    from signalrcore.hub_connection_builder import HubConnectionBuilder
    SIGNALR_AVAILABLE = True
except ImportError:
    SIGNALR_AVAILABLE = False
    print("[WARN] signalrcore not installed - SignalR disabled. Install with: pip install signalrcore")

# === Configuration ===
DARTGAME_API_URL = os.environ.get("DARTGAME_URL", "http://localhost:5000")
BOARD_ID = os.environ.get("BOARD_ID", "")
if not BOARD_ID:
    # Fetch current board from DartGame API
    try:
        import requests as _req
        _resp = _req.get(f"{DARTGAME_API_URL}/api/boards/current", timeout=5)
        if _resp.ok:
            BOARD_ID = _resp.json().get("id", "default")
            print(f"[INIT] Fetched board ID from API: {BOARD_ID}")
    except Exception as _e:
        print(f"[INIT] Could not fetch board ID: {_e}")
        BOARD_ID = "default"

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


# === Centralized Logging ===
def log_to_api(level: str, category: str, message: str, data: dict = None, game_id: str = None):
    """Send log entry to centralized DartGame API logging endpoint."""
    try:
        payload = {
            "source": "DartSensor",
            "level": level,
            "category": category,
            "message": message,
            "data": json.dumps(data) if data else None,
            "gameId": game_id
        }
        requests.post(f"{DARTGAME_API_URL}/api/logs", json=payload, timeout=0.5)
    except Exception as e:
        # Don't let logging failures break detection
        pass


# === Debug Image Logging ===
DEBUG_IMAGE_DIR = Path(r"C:\Users\clawd\DartImages")

def save_debug_images(event_name: str, frames: dict, dart_num: int = None, extra_info: str = None):
    """Save camera frames for later analysis.
    
    Creates timestamped folders with images from each camera.
    Example: C:/Users/clawd/DartImages/2026-02-04_20-18-43_dart1_single18/
    """
    try:
        DEBUG_IMAGE_DIR.mkdir(parents=True, exist_ok=True)
        
        timestamp = time.strftime("%Y-%m-%d_%H-%M-%S")
        folder_name = f"{timestamp}_{event_name}"
        if dart_num is not None:
            folder_name = f"{timestamp}_dart{dart_num}_{event_name}"
        if extra_info:
            folder_name += f"_{extra_info}"
        
        folder_path = DEBUG_IMAGE_DIR / folder_name
        folder_path.mkdir(parents=True, exist_ok=True)
        
        for cam_id, frame in frames.items():
            if frame is not None:
                img_path = folder_path / f"{cam_id}.png"
                cv2.imwrite(str(img_path), frame)
        
        logger.info(f"Saved debug images to {folder_path}")
    except Exception as e:
        logger.warning(f"Failed to save debug images: {e}")


# === SignalR Hub Connection ===
def capture_best_diff_frames(cameras, reference_frames: dict = None, num_frames: int = 8, delay_ms: int = 35) -> dict:
    """
    Capture multiple frames and pick the one with the STRONGEST diff per camera.
    
    WHY: Instead of picking the sharpest frame (which might be sharp but show the
    dart at a bad angle or mid-vibration), we pick the frame where the dart is
    MOST VISIBLE compared to the reference. This gives the strongest signal for
    the AI model to detect the dart's position.
    
    For each frame, we compute cv2.absdiff against the reference (baseline or
    previous state) and measure the total diff strength. The frame with the
    highest diff has the dart in the most distinct position.
    
    Cameras are read in parallel (threads) for each capture round.
    
    Args:
        cameras: List of Camera objects
        reference_frames: Dict of cam_id -> reference frame to diff against.
                         If None, falls back to sharpness-based selection.
        num_frames: Number of frames to capture per camera (default 8, ~280ms)
        delay_ms: Delay between capture rounds in ms (default 35ms)
    
    Returns:
        Dict of camera_id -> best-diff frame
    """
    import time
    import concurrent.futures
    import numpy as np
    
    DIFF_PIXEL_THRESHOLD = 16  # Pixels must differ by at least this to count
    
    # Collect frames with diff scores
    frame_lists = {f"cam{cam.index}": [] for cam in cameras}
    
    def _read_one(cam):
        ret, frame = cam.cap.read()
        return cam, ret, frame
    
    for i in range(num_frames):
        # Read all cameras in parallel
        with concurrent.futures.ThreadPoolExecutor(max_workers=len(cameras)) as executor:
            futures = [executor.submit(_read_one, cam) for cam in cameras]
            for f in concurrent.futures.as_completed(futures):
                cam, ret, frame = f.result()
                if ret and frame is not None:
                    cam_id = f"cam{cam.index}"
                    
                    # Compute diff score against reference frame
                    ref = reference_frames.get(cam_id) if reference_frames else None
                    if ref is not None:
                        # Count pixels that differ significantly from reference
                        # This measures how "visible" the dart is in this frame
                        gray_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
                        gray_ref = cv2.cvtColor(ref, cv2.COLOR_BGR2GRAY)
                        diff = cv2.absdiff(gray_frame, gray_ref)
                        # Count of pixels above threshold = dart visibility strength
                        diff_score = int(np.count_nonzero(diff > DIFF_PIXEL_THRESHOLD))
                    else:
                        # No reference available, fall back to sharpness
                        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
                        diff_score = cv2.Laplacian(gray, cv2.CV_64F).var()
                    
                    frame_lists[cam_id].append((frame.copy(), diff_score))
        if i < num_frames - 1:
            time.sleep(delay_ms / 1000.0)
    
    # Pick best-diff frame per camera
    best_frames = {}
    for cam_id, frames_with_scores in frame_lists.items():
        if frames_with_scores:
            best_frame, best_score = max(frames_with_scores, key=lambda x: x[1])
            best_frames[cam_id] = best_frame
            logger.info(f"[BEST-DIFF] {cam_id}: picked best of {len(frames_with_scores)} frames, score={best_score}")
    
    return best_frames


def capture_sharpest_frames(cameras, num_frames: int = 5, delay_ms: int = 30) -> dict:
    """Legacy wrapper — now uses best-diff selection (no reference = sharpness fallback)."""
    return capture_best_diff_frames(cameras, reference_frames=None, num_frames=num_frames, delay_ms=delay_ms)


def capture_averaged_frames(cameras, num_frames: int = 3, delay_ms: int = 50) -> dict:
    """Legacy wrapper — now uses best-diff selection."""
    return capture_best_diff_frames(cameras, reference_frames=None, num_frames=num_frames, delay_ms=delay_ms)

class HubConnection:
    """SignalR connection to DartGame API hub."""
    
    def __init__(self, hub_url: str, board_id: str, on_start_game, on_stop_game, on_rebase):
        self.hub_url = hub_url
        self.board_id = board_id
        self.on_start_game = on_start_game
        self.on_stop_game = on_stop_game
        self.on_rebase = on_rebase
        self.connection = None
        self.connected = False
        self._reconnect_thread = None
        self._stop_reconnect = False
    
    def connect(self):
        """Connect to SignalR hub."""
        if not SIGNALR_AVAILABLE:
            print("[HUB] SignalR not available")
            return False
        
        try:
            # Build SignalR hub URL
            signalr_url = f"{self.hub_url}/gamehub"
            print(f"[HUB] Connecting to {signalr_url}...")
            
            self.connection = HubConnectionBuilder()\
                .with_url(signalr_url)\
                .with_automatic_reconnect({
                    "type": "raw",
                    "keep_alive_interval": 10,
                    "reconnect_interval": 5,
                    "max_attempts": 10
                })\
                .build()
            
            # Register event handlers
            self.connection.on("StartGame", self._on_start_game)
            self.connection.on("StopGame", self._on_stop_game)
            self.connection.on("Rebase", self._on_rebase)
            self.connection.on("Registered", self._on_registered)
            
            # Connection lifecycle
            self.connection.on_open(self._on_open)
            self.connection.on_close(self._on_close)
            self.connection.on_error(self._on_error)
            
            # Start connection
            self.connection.start()
            return True
            
        except Exception as e:
            print(f"[HUB] Connection failed: {e}")
            return False
    
    def _on_open(self):
        """Called when connection opens."""
        print(f"[HUB] ========== CONNECTED ==========")
        self.connected = True
        # Re-fetch board ID if we fell back to default on startup
        if self.board_id == "default" or not self.board_id:
            try:
                import requests as _req
                _resp = _req.get(f"{DARTGAME_API_URL}/api/boards/current", timeout=5)
                if _resp.ok:
                    new_id = _resp.json().get("id", "default")
                    if new_id != "default":
                        global BOARD_ID
                        self.board_id = new_id
                        BOARD_ID = new_id
                        print(f"[HUB] Updated board ID to: {new_id}")
            except Exception as e:
                print(f"[HUB] Could not re-fetch board ID: {e}")
        # Register this board with the hub
        try:
            self.connection.send("RegisterBoard", [self.board_id])
            print(f"[HUB] Registering as board: {self.board_id}")
        except Exception as e:
            print(f"[HUB] Failed to register: {e}")
    
    def _on_close(self):
        """Called when connection closes."""
        print(f"[HUB] ========== DISCONNECTED ==========")
        self.connected = False
    
    def _on_error(self, error):
        """Called on connection error."""
        print(f"[HUB] Error: {error}")
    
    def _on_registered(self, args):
        """Called when hub confirms registration."""
        print(f"[HUB] Registered successfully: {args}")
    
    def _on_start_game(self, args):
        """Called when hub says to start game."""
        print(f"[HUB] ========== START GAME RECEIVED ==========")
        print(f"[HUB] Args: {args}")
        if self.on_start_game:
            self.on_start_game(args)
    
    def _on_stop_game(self, args):
        """Called when hub says to stop game."""
        print(f"[HUB] ========== STOP GAME RECEIVED ==========")
        if self.on_stop_game:
            self.on_stop_game(args)
    
    def _on_rebase(self, args):
        """Called when hub says to capture new baseline."""
        print(f"[HUB] ========== REBASE RECEIVED ==========")
        if self.on_rebase:
            self.on_rebase(args)
    
    def disconnect(self):
        """Disconnect from hub."""
        self._stop_reconnect = True
        if self.connection:
            try:
                self.connection.stop()
            except:
                pass
        self.connected = False


class DartGameClient:
    """HTTP client to send detections to DartGame API."""
    
    def __init__(self, base_url: str, board_id: str):
        self.base_url = base_url.rstrip('/')
        self.board_id = board_id
        self.session = requests.Session()
        self.session.headers["Content-Type"] = "application/json"
    
    def encode_image(self, frame: np.ndarray) -> str:
        """Encode frame as base64 PNG."""
        _, buffer = cv2.imencode('.png', frame)
        return base64.b64encode(buffer).decode('utf-8')
    
    def send_dart_images(self, frames: dict, dart_number: int = 1, before_frames: dict = None) -> dict:
        """
        Send camera frames to DartGame API for detection.
        frames: dict of {camera_id: numpy_frame} - current "after" frames
        dart_number: which dart this is (1, 2, or 3)
        before_frames: dict of {camera_id: numpy_frame} - frames from before dart landed
        """
        import time
        import uuid
        pipeline_start = time.time()
        epoch_ms = int(pipeline_start * 1000)
        request_id = str(uuid.uuid4())[:8]
        print(f"[TIMING][{request_id}] DS: Start dart {dart_number} @ epoch={epoch_ms}")
        
        try:
            images = []
            for cam_id, frame in frames.items():
                if frame is not None:
                    images.append({
                        "cameraId": cam_id,
                        "image": self.encode_image(frame)
                    })
            
            encode_time = (time.time() - pipeline_start) * 1000
            
            # Encode before frames if provided
            before_images = []
            if before_frames:
                for cam_id, frame in before_frames.items():
                    if frame is not None:
                        before_images.append({
                            "cameraId": cam_id,
                            "image": self.encode_image(frame)
                        })
            
            payload = {
                "boardId": self.board_id,
                "dartNumber": dart_number,
                "images": images,
                "beforeImages": before_images if before_images else None,  # For clean diff
                "requestId": request_id  # For cross-API timing correlation
            }
            
            print(f"[DART] Sending {len(images)} images to {self.base_url}/api/games/detect (dart {dart_number}) [encode: {encode_time:.0f}ms]")
            
            api_start = time.time()
            resp = self.session.post(
                f"{self.base_url}/api/games/detect",
                json=payload,
                timeout=5
            )
            api_time = (time.time() - api_start) * 1000
            total_time = (time.time() - pipeline_start) * 1000
            
            if resp.ok:
                result = resp.json()
                print(f"[DART] Response: {result}")
                epoch_end = int(time.time() * 1000)
                print(f"[TIMING][{request_id}] DS: Dart {dart_number} complete @ epoch={epoch_end} | encode={encode_time:.0f}ms, API={api_time:.0f}ms, TOTAL={total_time:.0f}ms")
                log_to_api("INFO", "Timing", f"Dart {dart_number} pipeline complete",
                          {"dart_num": dart_number, "encode_ms": round(encode_time), 
                           "api_ms": round(api_time), "total_ms": round(total_time)})
                return result
            else:
                print(f"[DART] Error: {resp.status_code} - {resp.text}")
                return {"error": resp.text}
                
        except Exception as e:
            print(f"[DART] Failed to send: {e}")
            return {"error": str(e)}
    
    def notify_board_clear(self):
        """Notify API that board was cleared."""
        try:
            print(f"[CLEAR] Notifying board clear")
            resp = self.session.post(
                f"{self.base_url}/api/games/events/clear",
                json={"boardId": self.board_id},
                timeout=2
            )
            if resp.ok:
                print(f"[CLEAR] Board cleared acknowledged")
        except Exception as e:
            print(f"[CLEAR] Failed: {e}")
    
    def health_check(self) -> bool:
        """Check if DartGame API is reachable."""
        try:
            resp = self.session.get(f"{self.base_url}/api/games/health", timeout=2)
            return resp.ok
        except:
            return False


@dataclass 
class CameraInfo:
    index: int
    cap: cv2.VideoCapture
    name: str
    last_frame: Optional[np.ndarray] = None
    frame_buffer: deque = field(default_factory=lambda: deque(maxlen=15))  # ~0.5s rolling buffer


class DartSensorUI:
    """DartSensor with visual debug UI - connects to DartGame API via SignalR."""
    
    def __init__(self):
        self.cameras: List[CameraInfo] = []
        self.detector = MultiCameraDartDetector(DetectorConfig())
        
        # API client for sending detections (HTTP)
        self.api_client = DartGameClient(DARTGAME_API_URL, BOARD_ID)
        print(f"[INIT] DartSensor for board '{BOARD_ID}'")
        print(f"[INIT] API URL: {DARTGAME_API_URL}")
        
        # Pre-warm the HTTP connection to avoid first-dart lag
        self._prewarm_connections()
        
        # SignalR connection for game events
        self.hub = HubConnection(
            DARTGAME_API_URL, 
            BOARD_ID,
            on_start_game=self._on_hub_start_game,
            on_stop_game=self._on_hub_stop_game,
            on_rebase=self._on_hub_rebase
        )
        
        # UI state
        self.selected_camera = 0
        self.show_debug = True
        self.game_started = False
        self.paused = False
        
        # Detection state
        self.detected_darts: List[dict] = []
        self.log_messages: List[str] = []
        
        # Stored baseline frames for dart 1 detection
        # WHY: When dart 1 is thrown, we need a clean "empty board" reference.
        # The regular previous frame might have the dart partially entering.
        # By storing a known-clean baseline at round start, dart 1 gets a
        # much cleaner diff signal, improving detection accuracy.
        self._stored_baseline_frames: dict = {}  # cam_id -> clean empty board frame
        
        # Window names
        self.main_window = "DartSensor Test UI"
        self.config_window = "Configuration"
        
        # Config sliders - thresholds lowered for better dart sensitivity
        self.config_values = {
            'base_threshold': 50,      # /10 = 5.0%
            'dart_threshold': 40,      # /10 = 4.0%
            'clear_threshold': 30,     # /10 = 3.0%
            'hand_threshold': 3000,    # /10 = 300.0%
            'clearing_threshold': 2000, # /10 = 200.0%
            'blur_kernel': 2,          # *2+1 = 5
            'drift_alpha': 5,          # /100 = 0.05
            'settling_ms': 150,
            'cooldown_ms': 300,
        }
    
    def _prewarm_connections(self):
        """Pre-warm HTTP connections and APIs to avoid first-dart lag."""
        import time
        print("[PREWARM] Warming up connections...")
        
        # 1. Ping DartGame API (establishes HTTP keep-alive)
        try:
            start = time.time()
            if self.api_client.health_check():
                print(f"[PREWARM] DartGame API: OK ({(time.time()-start)*1000:.0f}ms)")
            else:
                print("[PREWARM] DartGame API: NOT RESPONDING")
        except Exception as e:
            print(f"[PREWARM] DartGame API error: {e}")
        
        # 2. Warmup DartDetect via DartGame
        try:
            import requests
            start = time.time()
            resp = requests.post(f"{DARTGAME_API_URL}/api/games/warmup", timeout=10)
            if resp.ok:
                print(f"[PREWARM] DartDetect warmup: OK ({(time.time()-start)*1000:.0f}ms)")
            else:
                print(f"[PREWARM] DartDetect warmup: {resp.status_code}")
        except Exception as e:
            print(f"[PREWARM] DartDetect warmup error: {e}")
        
        print("[PREWARM] Done")
    
    def log(self, message: str):
        """Add message to log."""
        timestamp = time.strftime("%H:%M:%S")
        self.log_messages.append(f"[{timestamp}] {message}")
        if len(self.log_messages) > 20:
            self.log_messages.pop(0)
        logger.info(message)
    
    # === SignalR Event Handlers ===
    
    def _on_hub_start_game(self, args):
        """Hub says: start game, capture baseline."""
        self.log("Game started via SignalR - capturing baseline")
        self.game_started = True
        self.detector.dart_count = 0
        
        # Warmup the detection model immediately (one-time)
        threading.Thread(
            target=self._warmup_detect_model,
            daemon=True
        ).start()
        
        # Note: DartDetect now has its own continuous warmup (every 1.5s)
        # No need for periodic warmup from DartSensor
        
        # Capture baseline for all cameras immediately
        # Also store raw baseline frames for dart 1 best-diff selection
        for cam in self.cameras:
            if cam.last_frame is not None:
                cam_id = f"cam{cam.index}"
                self.detector.set_baseline(cam_id, cam.last_frame)
                self._stored_baseline_frames[cam_id] = cam.last_frame.copy()
        
        self.clear_board()
        cfg = self.detector.config
        print(f"[HUB] Baseline captured + stored for dart 1 diff, detection ACTIVE")
        print(f"[HUB] Thresholds: base={cfg.base_threshold_pct:.1f}%, dart={cfg.dart_threshold_pct:.1f}%, clear={cfg.clear_threshold_pct:.1f}%, clearing={cfg.clearing_start_pct:.1f}%")
    
    def _start_warmup_timer(self):
        """Start periodic warmup to keep model hot during game."""
        if hasattr(self, '_warmup_timer') and self._warmup_timer:
            self._warmup_timer.cancel()
        
        def warmup_loop():
            if self.game_started:
                self._warmup_detect_model()
                # Schedule next warmup in 20 seconds
                self._warmup_timer = threading.Timer(20.0, warmup_loop)
                self._warmup_timer.daemon = True
                self._warmup_timer.start()
        
        # Start first warmup timer
        self._warmup_timer = threading.Timer(20.0, warmup_loop)
        self._warmup_timer.daemon = True
        self._warmup_timer.start()
        print("[HUB] Warmup timer started (every 20s)")
    
    def _stop_warmup_timer(self):
        """Stop the periodic warmup timer."""
        if hasattr(self, '_warmup_timer') and self._warmup_timer:
            self._warmup_timer.cancel()
            self._warmup_timer = None
            print("[HUB] Warmup timer stopped")
    
    def _warmup_detect_model(self):
        """Call warmup endpoint on DartDetect API."""
        try:
            import requests
            resp = requests.post("http://localhost:8000/v1/warmup", timeout=10)
            if resp.ok:
                print("[HUB] DartDetect model warmed up")
            else:
                print(f"[HUB] Warmup failed: {resp.text}")
        except Exception as e:
            print(f"[HUB] Warmup error: {e}")
    
    def _on_hub_stop_game(self, args):
        """Hub says: stop game."""
        self.log("Game stopped via SignalR")
        self.game_started = False
        self._stop_warmup_timer()
        print(f"[HUB] Detection INACTIVE")
    
    def _on_hub_rebase(self, args):
        """Hub says: capture new baseline (darts removed)."""
        self.log("Rebase via SignalR - capturing new baseline")
        
        for cam in self.cameras:
            if cam.last_frame is not None:
                cam_id = f"cam{cam.index}"
                self.detector.set_baseline(cam_id, cam.last_frame)
                self._stored_baseline_frames[cam_id] = cam.last_frame.copy()
        
        self.detector.dart_count = 0
        self.clear_board()
        print(f"[HUB] Rebase complete + stored for dart 1 diff")
    
    def connect_to_hub(self):
        """Connect to SignalR hub."""
        if SIGNALR_AVAILABLE:
            threading.Thread(target=self.hub.connect, daemon=True).start()
        else:
            self.log("SignalR not available - manual mode only")
    
    def find_cameras(self, max_check: int = 10) -> List[int]:
        """Scan for available cameras."""
        available = []
        self.log(f"Scanning for cameras (0-{max_check-1})...")
        
        for i in range(max_check):
            cap = cv2.VideoCapture(i, cv2.CAP_DSHOW)
            if cap.isOpened():
                ret, frame = cap.read()
                if ret and frame is not None:
                    available.append(i)
                    self.log(f"  Found camera at index {i}")
                cap.release()
            time.sleep(0.3)
        
        self.log(f"Found {len(available)} camera(s)")
        return available
    
    def init_cameras(self, indices: List[int], width: int = 1280, height: int = 720, fps: int = 60):
        """Initialize cameras at specified indices."""
        self.log(f"Initializing {len(indices)} camera(s) at {width}x{height} @ {fps}fps...")
        
        for i, idx in enumerate(indices):
            # time.sleep(1.5)  # Staggered init - removed for faster startup
            cap = cv2.VideoCapture(idx, cv2.CAP_MSMF)
            
            if not cap.isOpened():
                cap = cv2.VideoCapture(idx, cv2.CAP_DSHOW)
            
            if cap.isOpened():
                cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
                cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
                cap.set(cv2.CAP_PROP_FPS, fps)  # Request target FPS
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                
                # Read back actual settings
                actual_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
                actual_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
                actual_fps = cap.get(cv2.CAP_PROP_FPS)
                
                ret, frame = cap.read()
                if ret:
                    self.cameras.append(CameraInfo(
                        index=idx,
                        cap=cap,
                        name=f"Camera {idx}",
                        last_frame=frame
                    ))
                    self.log(f"  Camera {idx}: {actual_w}x{actual_h} @ {actual_fps:.1f}fps")
                    print(f"[CAM] Camera {idx}: got {actual_w}x{actual_h}@{actual_fps:.1f}fps")
                else:
                    cap.release()
                    self.log(f"  Camera {idx} failed to grab frame")
            else:
                self.log(f"  Camera {idx} failed to open")
    
    def read_cameras(self):
        """Read frames from all cameras in parallel using threads.
        
        Sequential reads add ~30ms per camera (90ms for 3). Parallel reads
        capture all cameras simultaneously, reducing total time to ~30ms.
        This matters for dart detection — we want all cameras to see the
        dart at the same instant, not staggered by 60ms.
        """
        import concurrent.futures
        
        def _read_one(cam):
            ret, frame = cam.cap.read()
            return cam, ret, frame
        
        with concurrent.futures.ThreadPoolExecutor(max_workers=len(self.cameras)) as executor:
            futures = [executor.submit(_read_one, cam) for cam in self.cameras]
            for f in concurrent.futures.as_completed(futures):
                cam, ret, frame = f.result()
                if ret:
                    cam.last_frame = frame
                    cam.frame_buffer.append(frame.copy())  # Rolling buffer for before/after
    
    def get_primary_frame(self) -> Optional[np.ndarray]:
        """Get frame from selected camera."""
        if 0 <= self.selected_camera < len(self.cameras):
            return self.cameras[self.selected_camera].last_frame
        return None
    
    def update_detector_config(self):
        """Update detector config from slider values."""
        self.detector.config.base_threshold_pct = self.config_values['base_threshold'] / 10.0
        self.detector.config.dart_threshold_pct = self.config_values['dart_threshold'] / 10.0
        self.detector.config.clear_threshold_pct = self.config_values['clear_threshold'] / 10.0
        self.detector.config.hand_threshold_pct = self.config_values['hand_threshold'] / 10.0
        self.detector.config.clearing_start_pct = self.config_values['clearing_threshold'] / 10.0
        self.detector.config.blur_kernel_size = self.config_values['blur_kernel'] * 2 + 1
        self.detector.config.drift_blend_alpha = self.config_values['drift_alpha'] / 100.0
        self.detector.config.settling_ms = self.config_values['settling_ms']
        self.detector.config.cooldown_ms = self.config_values['cooldown_ms']
    
    def clear_board(self):
        """Clear detector state AND frame buffers for fresh start."""
        self.detector.clear_all_image1()
        for cam in self.cameras:
            cam.frame_buffer.clear()
        self.log("Board and frame buffers cleared")

    def create_config_window(self):
        """Create configuration window with trackbars."""
        cv2.namedWindow(self.config_window, cv2.WINDOW_NORMAL)
        cv2.resizeWindow(self.config_window, 500, 450)
        
        def nothing(x): pass
        
        # Threshold sliders (scaled values, divide by 10 for actual %)
        cv2.createTrackbar('Base %', self.config_window, 
                          self.config_values['base_threshold'], 500, nothing)
        cv2.createTrackbar('Dart %', self.config_window,
                          self.config_values['dart_threshold'], 500, nothing)
        cv2.createTrackbar('Clear %', self.config_window,
                          self.config_values['clear_threshold'], 200, nothing)
        cv2.createTrackbar('Hand %', self.config_window,
                          self.config_values['hand_threshold'], 5000, nothing)
        cv2.createTrackbar('Clearing %', self.config_window,
                          self.config_values['clearing_threshold'], 2000, nothing)
        cv2.createTrackbar('Blur', self.config_window,
                          self.config_values['blur_kernel'], 10, nothing)
        cv2.createTrackbar('Drift', self.config_window,
                          self.config_values['drift_alpha'], 50, nothing)
        cv2.createTrackbar('Settle ms', self.config_window,
                          self.config_values['settling_ms'], 500, nothing)
        cv2.createTrackbar('Cooldown ms', self.config_window,
                          self.config_values['cooldown_ms'], 1000, nothing)
    
    def draw_config_help(self):
        """Draw config explanations on the config window."""
        # Create a help image to display in the config window
        help_img = np.zeros((250, 500, 3), dtype=np.uint8)
        help_img[:] = (50, 50, 50)
        
        y = 20
        line_h = 20
        font = cv2.FONT_HERSHEY_SIMPLEX
        
        def put(text, color=(200, 200, 200)):
            nonlocal y
            cv2.putText(help_img, text, (10, y), font, 0.35, color, 1)
            y += line_h
        
        # Show actual computed values with explanations
        base_pct = self.config_values['base_threshold'] / 10.0
        dart_pct = self.config_values['dart_threshold'] / 10.0
        clear_pct = self.config_values['clear_threshold'] / 10.0
        hand_pct = self.config_values['hand_threshold'] / 10.0
        clearing_pct = self.config_values['clearing_threshold'] / 10.0
        blur_k = self.config_values['blur_kernel'] * 2 + 1
        drift_a = self.config_values['drift_alpha'] / 100.0
        
        put(f"Base %: {base_pct:.1f}% - Diff to detect dart landed", (0, 255, 255))
        put(f"Dart %: {dart_pct:.1f}% - Diff to detect NEW dart (vs Image1)", (0, 255, 255))
        put(f"Clear %: {clear_pct:.1f}% - Max diff to consider board clear", (0, 255, 255))
        put(f"Hand %: {hand_pct:.1f}% - Diff = hand blocking camera (ignore)", (0, 200, 200))
        put(f"Clearing %: {clearing_pct:.1f}% - Diff = pulling darts (ignore)", (0, 200, 200))
        put(f"Blur: {blur_k}x{blur_k} - Gaussian blur for noise reduction", (0, 255, 255))
        put(f"Drift: {drift_a:.0%} - Lighting adaptation speed", (0, 255, 255))
        put(f"Settle: {self.config_values['settling_ms']}ms - Wait after motion", (0, 255, 255))
        put(f"Cooldown: {self.config_values['cooldown_ms']}ms - Min time between detections", (0, 255, 255))
        y += 5
        put("Lower % = more sensitive | Higher % = fewer false positives", (150, 150, 150))
        
        cv2.imshow(self.config_window, help_img)
    
    def read_config_trackbars(self):
        """Read current trackbar values."""
        self.config_values['base_threshold'] = cv2.getTrackbarPos('Base %', self.config_window)
        self.config_values['dart_threshold'] = cv2.getTrackbarPos('Dart %', self.config_window)
        self.config_values['clear_threshold'] = cv2.getTrackbarPos('Clear %', self.config_window)
        self.config_values['hand_threshold'] = cv2.getTrackbarPos('Hand %', self.config_window)
        self.config_values['clearing_threshold'] = cv2.getTrackbarPos('Clearing %', self.config_window)
        self.config_values['blur_kernel'] = max(1, cv2.getTrackbarPos('Blur', self.config_window))
        self.config_values['drift_alpha'] = cv2.getTrackbarPos('Drift', self.config_window)
        self.config_values['settling_ms'] = cv2.getTrackbarPos('Settle ms', self.config_window)
        self.config_values['cooldown_ms'] = cv2.getTrackbarPos('Cooldown ms', self.config_window)
        self.update_detector_config()
    
    def create_info_panel(self, width: int, height: int) -> np.ndarray:
        """Create the right-side info panel."""
        panel = np.zeros((height, width, 3), dtype=np.uint8)
        panel[:] = (40, 40, 40)
        
        y = 30
        line_height = 25
        
        def put_text(text, color=(255, 255, 255), size=0.6):
            nonlocal y
            cv2.putText(panel, text, (10, y), cv2.FONT_HERSHEY_SIMPLEX, size, color, 1)
            y += line_height
        
        # Title
        put_text("=== DartSensor Test ===", (0, 255, 255), 0.7)
        y += 10
        
        # Game state
        if self.game_started:
            put_text("Game: STARTED", (0, 255, 0))
        else:
            put_text("Game: STOPPED", (0, 0, 255))
        
        put_text(f"Camera: {self.selected_camera + 1}/{len(self.cameras)}")
        put_text(f"Debug: {'ON' if self.show_debug else 'OFF'}")
        y += 10
        
        # Detector state
        put_text("--- Detector ---", (255, 255, 0))
        put_text(f"Darts detected: {self.detector.dart_count}")
        
        # Count cameras with baselines/image1
        cams_with_base = sum(1 for c in self.detector.cameras.values() if c.base_image is not None)
        cams_with_img1 = sum(1 for c in self.detector.cameras.values() if c.image1 is not None)
        total_cams = len(self.detector.cameras)
        put_text(f"Baselines: {cams_with_base}/{total_cams}")
        put_text(f"Image1s: {cams_with_img1}/{total_cams}")
        put_text(f"Min agree: {self.detector.config.min_cameras_agree}")
        y += 10
        
        # Config summary
        put_text("--- Config ---", (255, 255, 0))
        put_text(f"Base thresh: {self.detector.config.base_threshold_pct:.1f}%")
        put_text(f"Dart thresh: {self.detector.config.dart_threshold_pct:.1f}%")
        put_text(f"Clear thresh: {self.detector.config.clear_threshold_pct:.1f}%")
        y += 10
        
        # Controls
        put_text("--- Controls ---", (255, 255, 0))
        put_text("[SPACE] Start/Stop game")
        put_text("[B] Capture baseline")
        put_text("[C] Clear Image1")
        put_text("[R] Reset all")
        put_text("[D] Toggle debug view")
        put_text("[1-3] Select camera")
        put_text("[S] Save config")
        put_text("[Q] Quit")
        y += 10
        
        # Log
        put_text("--- Log ---", (255, 255, 0))
        for msg in self.log_messages[-6:]:
            put_text(msg[:45], (180, 180, 180), 0.4)
            y += -5  # Tighter spacing for log
        
        return panel
    
    def create_reference_panel(self, width: int, height: int) -> np.ndarray:
        """Create panel showing baseline and Image1 for selected camera."""
        panel = np.zeros((height, width, 3), dtype=np.uint8)
        panel[:] = (30, 30, 30)
        
        thumb_h = height // 2 - 20
        thumb_w = width - 20
        
        # Get selected camera state
        selected_cam_id = None
        cam_state = None
        if self.cameras and self.selected_camera < len(self.cameras):
            selected_cam_id = f"cam{self.cameras[self.selected_camera].index}"
            cam_state = self.detector.get_camera_state(selected_cam_id)
        
        # Baseline thumbnail
        cv2.putText(panel, f"Baseline ({selected_cam_id}):", (10, 20), 
                   cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1)
        if cam_state is not None and cam_state.base_image_raw is not None:
            thumb = cv2.resize(cam_state.base_image_raw, (thumb_w, thumb_h))
            panel[30:30+thumb_h, 10:10+thumb_w] = thumb
        else:
            cv2.putText(panel, "Not set", (10, 30 + thumb_h//2),
                       cv2.FONT_HERSHEY_SIMPLEX, 0.6, (100, 100, 100), 1)
        
        # Image1 thumbnail
        y_offset = height // 2 + 10
        cv2.putText(panel, f"Image1 ({selected_cam_id}):", (10, y_offset), 
                   cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1)
        if cam_state is not None and cam_state.image1_raw is not None:
            thumb = cv2.resize(cam_state.image1_raw, (thumb_w, thumb_h))
            panel[y_offset+10:y_offset+10+thumb_h, 10:10+thumb_w] = thumb
        else:
            cv2.putText(panel, "Not set (board clear)", (10, y_offset + thumb_h//2),
                       cv2.FONT_HERSHEY_SIMPLEX, 0.4, (100, 100, 100), 1)
        
        return panel
    
    def run(self):
        """Main UI loop."""
        # Find and init cameras
        available = self.find_cameras()
        if not available:
            self.log("No cameras found!")
            return
        
        # Use up to 3 cameras
        self.init_cameras(available[:3])
        if not self.cameras:
            self.log("Failed to initialize any cameras!")
            return
        
        # Connect to SignalR hub
        self.connect_to_hub()
        
        # Create windows
        cv2.namedWindow(self.main_window, cv2.WINDOW_NORMAL)
        self.create_config_window()
        
        self.log("UI ready - waiting for game start via SignalR (or press SPACE for manual)")
        
        settling_start = None
        
        try:
            while True:
                # Read config
                self.read_config_trackbars()
                
                # Update config help window
                self.draw_config_help()
                
                # Read cameras
                if not self.paused:
                    self.read_cameras()
                
                frame = self.get_primary_frame()
                if frame is None:
                    time.sleep(0.1)
                    continue
                
                # Build frames dict for all cameras
                all_frames = {}
                for cam in self.cameras:
                    if cam.last_frame is not None:
                        cam_id = f"cam{cam.index}"
                        all_frames[cam_id] = cam.last_frame
                
                # Check if we have baselines
                has_baselines = len(self.detector.cameras) > 0 and any(
                    c.base_image is not None for c in self.detector.cameras.values()
                )
                
                # Always process frames to update diff values (for display)
                # but only act on detection when game is started
                result = None
                if has_baselines and all_frames:
                    result = self.detector.process_frames(all_frames)
                    
                    # Only handle dart detection if game is started
                    if self.game_started:
                        # Check if we're in clearing mode - block dart detection
                        in_clearing_mode = hasattr(self, '_clearing_mode') and self._clearing_mode
                        
                        # Hand in frame = someone reaching in, start clearing mode
                        if result.state == BoardState.HAND_IN_FRAME:
                            settling_start = None
                            if self.detector.dart_count > 0 and not in_clearing_mode:
                                self.log("Hand detected - entering clearing mode")
                                log_to_api("INFO", "Clearing", "Hand detected - entering clearing mode", 
                                          {"dart_count": self.detector.dart_count, "state": "HAND_IN_FRAME"})
                                self._clearing_mode = True
                                self._clearing_start = time.time()
                        
                        # Clearing = pulling darts, stay in clearing mode
                        elif result.state == BoardState.CLEARING:
                            settling_start = None
                            if self.detector.dart_count > 0 and not in_clearing_mode:
                                self.log("Clearing detected - entering clearing mode")
                                log_to_api("INFO", "Clearing", "Clearing detected - entering clearing mode",
                                          {"dart_count": self.detector.dart_count, "state": "CLEARING"})
                                self._clearing_mode = True
                                self._clearing_start = time.time()
                        
                        # Board cleared - if we were in clearing mode, start confirmation timer
                        elif result.state == BoardState.BOARD_CLEARED or result.state == BoardState.CLEAR:
                            settling_start = None
                            if in_clearing_mode:
                                if not hasattr(self, '_clear_confirm_start'):
                                    self.log("Board appears clear - waiting 1s to confirm...")
                                    log_to_api("INFO", "Clearing", "Board appears clear - waiting 1s to confirm")
                                    self._clear_confirm_start = time.time()
                                else:
                                    elapsed = time.time() - self._clear_confirm_start
                                    if elapsed >= 1.0:
                                        # Confirmed cleared after 1 second
                                        clearing_duration = time.time() - self._clearing_start if hasattr(self, '_clearing_start') else 0
                                        self.log("Board cleared confirmed!")
                                        log_to_api("INFO", "Clearing", "Board cleared confirmed!", 
                                                  {"clearing_duration_sec": round(clearing_duration, 2)})
                                        print("[CLEAR] ========== BOARD CLEARED (CONFIRMED) ==========")
                                        # Notify DartGame board is clear (so it advances the turn)
                                        # but do NOT rebase here - wait for DartGame to send Rebase via SignalR
                                        self.detector.dart_count = 0
                                        self.clear_board()
                                        threading.Thread(
                                            target=self.api_client.notify_board_clear,
                                            daemon=True
                                        ).start()
                                        # Reset clearing state
                                        del self._clearing_mode
                                        del self._clear_confirm_start
                                        if hasattr(self, '_clearing_start'):
                                            del self._clearing_start
                            else:
                                # Not in clearing mode - just log for debug
                                pass
                        
                        # HAS_DARTS state - reset clear confirmation if we see darts again
                        elif result.state == BoardState.HAS_DARTS:
                            settling_start = None
                            if hasattr(self, '_clear_confirm_start'):
                                self.log("Clear confirmation cancelled - darts detected")
                                log_to_api("WARN", "Clearing", "Clear confirmation cancelled - darts still detected")
                                del self._clear_confirm_start
                        
                        # New dart detection - ONLY if not in clearing mode
                        # Also: if we already have 3 darts, ANY motion should trigger clearing mode
                        elif result.state == BoardState.NEW_DART and not in_clearing_mode:
                            # After 3 darts, treat new motion as clearing (hand reaching in to pull)
                            if self.detector.dart_count >= 3:
                                self.log(f"Motion after 3 darts - entering clearing mode (blocking false Dart 4)")
                                log_to_api("INFO", "Clearing", "Motion after 3 darts - auto-entering clearing mode",
                                          {"dart_count": self.detector.dart_count})
                                self._clearing_mode = True
                                self._clearing_start = time.time()
                                settling_start = None
                            elif settling_start is None:
                                settling_start = time.time()
                                self.log(f"Motion detected, settling...")
                                # IMPORTANT: Save "before" frames NOW, when motion is first detected
                                # This captures the board state BEFORE the dart landed
                                self._before_frames_for_next_dart = {}
                                for cam in self.cameras:
                                    cam_id = f"cam{cam.index}"
                                    if len(cam.frame_buffer) >= 2:
                                        # Use the second-to-last frame (most recent clean frame)
                                        self._before_frames_for_next_dart[cam_id] = cam.frame_buffer[-2].copy()
                                    elif len(cam.frame_buffer) > 0:
                                        self._before_frames_for_next_dart[cam_id] = cam.frame_buffer[-1].copy()
                            elif (time.time() - settling_start) * 1000 >= self.detector.config.settling_ms:
                                # Settled - capture dart
                                self.detector.dart_count += 1
                                self.detector.set_all_image1(all_frames)
                                self.detector.mark_detection()
                                dart_num = self.detector.dart_count
                                self.log(f"DART {dart_num} captured!")
                                print(f"[DART] ========== DART {dart_num} DETECTED ==========")
                                log_to_api("INFO", "Detection", f"Dart {dart_num} detected by DartSensor",
                                          {"dart_num": dart_num, "settling_ms": self.detector.config.settling_ms})
                                
                                # Save debug images for later analysis
                                # save_debug_images("detected", all_frames, dart_num)  # Disabled for now
                                
                                # === BEST-DIFF FRAME SELECTION ===
                                # Capture 8 frames over ~280ms, pick the frame where the dart
                                # is MOST VISIBLE compared to a reference image. This is better
                                # than sharpness-based selection because a sharp frame might show
                                # the dart at a bad angle, while best-diff finds the frame where
                                # the dart creates the strongest visual change.
                                before_frames = getattr(self, '_before_frames_for_next_dart', {})
                                
                                # === STORED BASELINE FOR DART 1 ===
                                # For dart 1, use the stored clean baseline (captured at game/round
                                # start when the board is empty). This gives a MUCH cleaner diff
                                # than using the regular previous frame, which might have the dart
                                # partially entering or motion blur from the throw.
                                if dart_num == 1 and self._stored_baseline_frames:
                                    diff_reference = self._stored_baseline_frames
                                    before_frames = self._stored_baseline_frames  # Send to DartDetect too
                                    self.log("Dart 1: using stored baseline for diff reference")
                                else:
                                    diff_reference = before_frames if before_frames else None
                                
                                frames_to_send = capture_best_diff_frames(
                                    self.cameras,
                                    reference_frames=diff_reference,
                                    num_frames=8,
                                    delay_ms=35
                                )
                                
                                if not before_frames:
                                    # Fallback: try frame buffer (less reliable)
                                    self.log("WARNING: No saved before_frames, using frame buffer fallback")
                                    for cam in self.cameras:
                                        cam_id = f"cam{cam.index}"
                                        if len(cam.frame_buffer) >= 5:
                                            before_frames[cam_id] = cam.frame_buffer[-5]
                                        elif len(cam.frame_buffer) > 0:
                                            before_frames[cam_id] = cam.frame_buffer[0]
                                
                                threading.Thread(
                                    target=self.api_client.send_dart_images,
                                    args=(frames_to_send, dart_num, before_frames),
                                    daemon=True
                                ).start()
                                
                                settling_start = None
                        
                        # Any other state while in clearing mode - check for timeout
                        elif in_clearing_mode:
                            settling_start = None
                            # If we've been clearing for more than 10 seconds, something's wrong - reset
                            if hasattr(self, '_clearing_start') and (time.time() - self._clearing_start) > 10.0:
                                self.log("Clearing timeout - resetting")
                                log_to_api("WARN", "Clearing", "Clearing timeout after 10s - force resetting",
                                          {"clearing_duration_sec": 10.0})
                                self.detector.dart_count = 0
                                self.clear_board()
                                del self._clearing_mode
                                if hasattr(self, '_clear_confirm_start'):
                                    del self._clear_confirm_start
                                del self._clearing_start
                        else:
                            settling_start = None
                
                # Build display - use selected camera
                selected_cam_id = f"cam{self.cameras[self.selected_camera].index}" if self.cameras else "cam0"
                if self.show_debug and has_baselines:
                    main_view = self.detector.get_debug_frame(selected_cam_id, frame)
                else:
                    main_view = frame.copy()
                
                # Overlay state indicator
                if result:
                    state_colors = {
                        BoardState.CLEAR: ((0, 255, 0), "CLEAR"),
                        BoardState.HAS_DARTS: ((255, 255, 0), "HAS DARTS"),
                        BoardState.NEW_DART: ((0, 165, 255), "NEW DART!"),
                        BoardState.BOARD_CLEARED: ((255, 0, 255), "CLEARED"),
                        BoardState.HAND_IN_FRAME: ((0, 0, 255), "HAND!"),
                        BoardState.CLEARING: ((0, 100, 255), "CLEARING..."),
                    }
                    color, text = state_colors.get(result.state, ((255, 255, 255), "???"))
                    cv2.putText(main_view, text, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 1.0, color, 2)
                
                # Resize main view
                main_h = 480
                main_w = int(main_h * frame.shape[1] / frame.shape[0])
                main_view = cv2.resize(main_view, (main_w, main_h))
                
                # Create side panels
                info_panel = self.create_info_panel(300, main_h)
                ref_panel = self.create_reference_panel(200, main_h)
                
                # Combine
                display = np.hstack([ref_panel, main_view, info_panel])
                
                # Show camera thumbnails at bottom if multiple cameras
                if len(self.cameras) > 1:
                    thumb_row = []
                    thumb_size = (160, 120)
                    for i, cam in enumerate(self.cameras):
                        if cam.last_frame is not None:
                            thumb = cv2.resize(cam.last_frame, thumb_size)
                            # Highlight selected
                            if i == self.selected_camera:
                                cv2.rectangle(thumb, (0, 0), (thumb_size[0]-1, thumb_size[1]-1), 
                                            (0, 255, 0), 3)
                            cv2.putText(thumb, f"Cam {i+1}", (5, 20),
                                       cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1)
                            
                            # Add diff percentages overlay
                            cam_id = f"cam{cam.index}"
                            cam_state = self.detector.get_camera_state(cam_id)
                            if cam_state is not None:
                                diff_base = cam_state.last_diff_base
                                diff_img1 = cam_state.last_diff_img1
                                # Color code: green=low, yellow=medium, red=high
                                base_color = (0, 255, 0) if diff_base < 1.5 else (0, 255, 255) if diff_base < 3.0 else (0, 0, 255)
                                img1_color = (0, 255, 0) if diff_img1 < 1.5 else (0, 255, 255) if diff_img1 < 3.0 else (0, 0, 255)
                                cv2.putText(thumb, f"B:{diff_base:.1f}%", (5, 40),
                                           cv2.FONT_HERSHEY_SIMPLEX, 0.4, base_color, 1)
                                cv2.putText(thumb, f"I:{diff_img1:.1f}%", (5, 55),
                                           cv2.FONT_HERSHEY_SIMPLEX, 0.4, img1_color, 1)
                            
                            thumb_row.append(thumb)
                    
                    if thumb_row:
                        thumb_strip = np.hstack(thumb_row)
                        # Pad to match display width
                        if thumb_strip.shape[1] < display.shape[1]:
                            pad = np.zeros((thumb_size[1], display.shape[1] - thumb_strip.shape[1], 3), dtype=np.uint8)
                            thumb_strip = np.hstack([thumb_strip, pad])
                        elif thumb_strip.shape[1] > display.shape[1]:
                            thumb_strip = thumb_strip[:, :display.shape[1]]
                        
                        display = np.vstack([display, thumb_strip])
                
                cv2.imshow(self.main_window, display)
                
                # Handle keys
                key = cv2.waitKey(16) & 0xFF
                
                if key == ord('q'):
                    break
                elif key == ord(' '):
                    self.game_started = not self.game_started
                    if self.game_started:
                        self.log("Game STARTED")
                        if not has_baselines and all_frames:
                            self.detector.set_all_baselines(all_frames)
                            self.log(f"Auto-captured baselines ({len(all_frames)} cameras)")
                    else:
                        self.log("Game STOPPED")
                elif key == ord('b'):
                    if all_frames:
                        self.detector.set_all_baselines(all_frames)
                        self.log(f"Baselines captured ({len(all_frames)} cameras)")
                elif key == ord('c'):
                    self.clear_board()
                elif key == ord('r'):
                    if all_frames:
                        self.detector.set_all_baselines(all_frames)
                        self.clear_board()
                        self.detected_darts = []
                        self.log("Reset complete")
                elif key == ord('d'):
                    self.show_debug = not self.show_debug
                elif key == ord('p'):
                    self.paused = not self.paused
                    self.log("PAUSED" if self.paused else "RESUMED")
                elif key == ord('s'):
                    self.save_config()
                elif key in [ord('1'), ord('2'), ord('3')]:
                    idx = key - ord('1')
                    if idx < len(self.cameras):
                        self.selected_camera = idx
                        self.log(f"Selected camera {idx + 1}")
        
        finally:
            for cam in self.cameras:
                cam.cap.release()
            cv2.destroyAllWindows()
    
    def save_config(self):
        """Save current config to file."""
        config_path = Path(__file__).parent.parent / "config" / "settings.yaml"
        config_path.parent.mkdir(parents=True, exist_ok=True)
        
        config = {
            'detection': {
                'base_threshold_pct': self.detector.config.base_threshold_pct,
                'dart_threshold_pct': self.detector.config.dart_threshold_pct,
                'clear_threshold_pct': self.detector.config.clear_threshold_pct,
                'blur_kernel_size': self.detector.config.blur_kernel_size,
                'drift_blend_alpha': self.detector.config.drift_blend_alpha,
                'settling_ms': self.detector.config.settling_ms,
                'cooldown_ms': self.detector.config.cooldown_ms,
            },
            'cameras': [
                {'id': f'cam{cam.index}', 'device': cam.index}
                for cam in self.cameras
            ]
        }
        
        import yaml
        with open(config_path, 'w') as f:
            yaml.dump(config, f, default_flow_style=False)
        
        self.log(f"Config saved to {config_path}")


# === HTTP API Server (runs in background thread) ===
from flask import Flask, jsonify, request
from flask_cors import CORS
import threading

# Global reference to the UI (set in main)
_sensor_ui = None

api = Flask(__name__)
CORS(api)  # Enable CORS for browser access
api.logger.setLevel(logging.WARNING)  # Reduce Flask noise

@api.route('/health', methods=['GET'])
def health():
    return jsonify({"status": "ok", "game_started": _sensor_ui.game_started if _sensor_ui else False})

@api.route('/start', methods=['POST'])
def start_game():
    """Called by DartGame API when a game starts - triggers baseline capture."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    print("[API] ========== START GAME RECEIVED ==========")
    _sensor_ui.game_started = True
    _sensor_ui.detector.dart_count = 0
    
    # Capture baseline for all cameras + store for dart 1 diff
    frames = {f"cam{cam.index}": cam.last_frame for cam in _sensor_ui.cameras if cam.last_frame is not None}
    for cam_id, frame in frames.items():
        if frame is not None:
            _sensor_ui.detector.set_baseline(cam_id, frame)
            _sensor_ui._stored_baseline_frames[cam_id] = frame.copy()
    
    _sensor_ui.detector.clear_all_image1()
    _sensor_ui.log("Game started - baseline captured + stored for dart 1")
    print(f"[API] Baseline captured + stored for {len(frames)} cameras")
    
    return jsonify({"status": "started", "cameras": len(frames)})

@api.route('/stop', methods=['POST'])
def stop_game():
    """Called when game ends."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    print("[API] ========== STOP GAME RECEIVED ==========")
    _sensor_ui.game_started = False
    _sensor_ui.log("Game stopped")
    
    return jsonify({"status": "stopped"})

@api.route('/rebase', methods=['POST'])
def rebase():
    """Capture new baseline (e.g., after darts removed)."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    print("[API] ========== REBASE RECEIVED ==========")
    frames = {f"cam{cam.index}": cam.last_frame for cam in _sensor_ui.cameras if cam.last_frame is not None}
    for cam_id, frame in frames.items():
        if frame is not None:
            _sensor_ui.detector.set_baseline(cam_id, frame)
            _sensor_ui._stored_baseline_frames[cam_id] = frame.copy()
    
    _sensor_ui.detector.dart_count = 0
    _sensor_ui.detector.clear_all_image1()
    _sensor_ui.log("Rebase complete + stored for dart 1")
    print(f"[API] Rebase complete + stored for {len(frames)} cameras")
    
    return jsonify({"status": "rebased", "cameras": len(frames)})

@api.route('/config', methods=['GET'])
def get_config():
    """Get current detection config."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    cfg = _sensor_ui.detector.config
    return jsonify({
        "base_threshold_pct": cfg.base_threshold_pct,
        "dart_threshold_pct": cfg.dart_threshold_pct,
        "clear_threshold_pct": cfg.clear_threshold_pct,
        "settling_ms": cfg.settling_ms,
        "cooldown_ms": cfg.cooldown_ms,
    })

@api.route('/config', methods=['PUT'])
def update_config():
    """Update detection config."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    data = request.json or {}
    cfg = _sensor_ui.detector.config
    
    if 'base_threshold_pct' in data:
        cfg.base_threshold_pct = float(data['base_threshold_pct'])
    if 'dart_threshold_pct' in data:
        cfg.dart_threshold_pct = float(data['dart_threshold_pct'])
    if 'clear_threshold_pct' in data:
        cfg.clear_threshold_pct = float(data['clear_threshold_pct'])
    if 'settling_ms' in data:
        cfg.settling_ms = int(data['settling_ms'])
    if 'cooldown_ms' in data:
        cfg.cooldown_ms = int(data['cooldown_ms'])
    
    print(f"[API] Config updated: {data}")
    _sensor_ui.log(f"Config updated via API")
    
    return jsonify({"status": "updated"})

@api.route('/status', methods=['GET'])
def status():
    """Get sensor status."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    cam_count = len(_sensor_ui.cameras)
    return jsonify({
        "game_started": _sensor_ui.game_started,
        "dart_count": _sensor_ui.detector.dart_count,
        "cameras": cam_count,
        "baselines_captured": sum(1 for c in _sensor_ui.detector.cameras.values() if c.base_image is not None),
        "ready": cam_count > 0
    })



@api.route('/cameras/<int:cam_index>/snapshot', methods=['GET'])
def get_camera_snapshot(cam_index):
    """Get a JPEG snapshot from a camera as JSON with base64."""
    from flask import Response, request
    
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    # Find the camera by index
    camera = None
    for cam in _sensor_ui.cameras:
        if cam.index == cam_index:
            camera = cam
            break
    
    if camera is None:
        return jsonify({"error": f"Camera {cam_index} not found"}), 404
    
    if camera.last_frame is None:
        return jsonify({"error": f"Camera {cam_index} has no frame"}), 503
    
    # Encode frame as PNG
    _, buffer = cv2.imencode('.png', camera.last_frame)
    
    # Check if client wants raw image or JSON
    if request.args.get('raw') == 'true':
        return Response(buffer.tobytes(), mimetype='image/jpeg')
    
    # Default: return JSON with base64 (for web UI)
    import base64
    b64_image = base64.b64encode(buffer.tobytes()).decode('utf-8')
    return jsonify({"image": b64_image, "cameraIndex": cam_index})


@api.route('/cameras', methods=['GET'])
def list_cameras():
    """List available cameras."""
    if _sensor_ui is None:
        return jsonify({"error": "Sensor not initialized"}), 500
    
    cameras = []
    for cam in _sensor_ui.cameras:
        cameras.append({
            "index": cam.index,
            "hasFrame": cam.last_frame is not None
        })
    
    return jsonify(cameras)

def run_api_server(port=8001):
    """Run Flask API in background."""
    print(f"[API] Starting sensor API on port {port}")
    api.run(host='0.0.0.0', port=port, threaded=True, use_reloader=False)


def main():
    global _sensor_ui
    
    # Start API server in background
    api_thread = threading.Thread(target=run_api_server, args=(8001,), daemon=True)
    api_thread.start()
    
    ui = DartSensorUI()
    _sensor_ui = ui  # Set global reference for API endpoints
    ui.run()


if __name__ == "__main__":
    main()


