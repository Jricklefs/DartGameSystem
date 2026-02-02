"""
DartSensor main entry point.
State machine for dart detection and server communication.
"""

import time
import logging
import yaml
import signal
import sys
from enum import Enum, auto
from pathlib import Path
from typing import Optional

from camera import CameraManager, CameraConfig
from detector import FrameDetector, DetectionConfig
from client import DetectionClient, ServerConfig

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


class State(Enum):
    """Detection state machine states."""
    IDLE = auto()                  # Not active, waiting for game start
    CAPTURING_BASELINE = auto()    # Capturing baseline frame
    WAITING_FOR_DART = auto()      # Watching for dart to hit
    SETTLING = auto()              # Dart detected, waiting for it to settle
    SENDING = auto()               # Sending snapshot to server
    WAITING_FOR_CLEAR = auto()     # Waiting for darts to be removed


class DartSensor:
    """Main dart detection application."""
    
    def __init__(self, config_path: str = "config/settings.yaml"):
        self.config = self._load_config(config_path)
        self.state = State.IDLE
        self.dart_count = 0
        self.max_darts = 3  # Configurable per game type
        
        # Initialize components
        self.cameras = self._init_cameras()
        self.detector = self._init_detector()
        self.client = self._init_client()
        
        # Timing
        self._settling_start: Optional[float] = None
        self._running = False
    
    def _load_config(self, path: str) -> dict:
        """Load configuration from YAML file."""
        config_path = Path(path)
        if not config_path.exists():
            logger.error(f"Config file not found: {path}")
            sys.exit(1)
        
        with open(config_path) as f:
            return yaml.safe_load(f)
    
    def _init_cameras(self) -> CameraManager:
        """Initialize camera manager from config."""
        cam_configs = []
        for cam in self.config.get("cameras", []):
            cam_configs.append(CameraConfig(
                id=cam["id"],
                device=cam["device"],
                width=cam.get("width", 1280),
                height=cam.get("height", 720),
                fps=cam.get("fps", 30)
            ))
        return CameraManager(cam_configs)
    
    def _init_detector(self) -> FrameDetector:
        """Initialize frame detector from config."""
        det_cfg = self.config.get("detection", {})
        return FrameDetector(DetectionConfig(
            diff_threshold=det_cfg.get("diff_threshold", 25),
            min_contour_area=det_cfg.get("min_contour_area", 500),
            max_contour_area=det_cfg.get("max_contour_area", 50000),
            settling_ms=det_cfg.get("settling_ms", 150),
            cooldown_ms=det_cfg.get("cooldown_ms", 500),
            baseline_match_pct=det_cfg.get("baseline_match_pct", 97.0),
            baseline_update_on_clear=det_cfg.get("baseline_update_on_clear", True)
        ))
    
    def _init_client(self) -> DetectionClient:
        """Initialize detection client from config."""
        srv_cfg = self.config.get("server", {})
        board_id = self.config.get("board", {}).get("id", "board1")
        return DetectionClient(
            ServerConfig(
                url=srv_cfg.get("url", "http://localhost:8000"),
                api_key=srv_cfg.get("api_key", ""),
                timeout_ms=srv_cfg.get("timeout_ms", 5000)
            ),
            board_id=board_id
        )
    
    def start(self):
        """Start the sensor."""
        logger.info("Starting DartSensor...")
        
        # Open cameras
        if not self.cameras.open_all():
            logger.error("Failed to open all cameras")
            return
        
        # Check server connection
        if not self.client.health_check():
            logger.warning("Detection server not reachable - continuing anyway")
        
        self._running = True
        self._transition(State.CAPTURING_BASELINE)
        
        # Main loop
        try:
            while self._running:
                self._tick()
                time.sleep(0.016)  # ~60fps check rate
        except KeyboardInterrupt:
            logger.info("Interrupted")
        finally:
            self.stop()
    
    def stop(self):
        """Stop the sensor."""
        self._running = False
        self.cameras.close_all()
        logger.info("DartSensor stopped")
    
    def _transition(self, new_state: State):
        """Transition to a new state."""
        logger.debug(f"State: {self.state.name} -> {new_state.name}")
        self.state = new_state
    
    def _tick(self):
        """Main loop tick - process current state."""
        
        if self.state == State.IDLE:
            # Waiting for external trigger to start
            pass
        
        elif self.state == State.CAPTURING_BASELINE:
            self._handle_baseline_capture()
        
        elif self.state == State.WAITING_FOR_DART:
            self._handle_waiting_for_dart()
        
        elif self.state == State.SETTLING:
            self._handle_settling()
        
        elif self.state == State.SENDING:
            self._handle_sending()
        
        elif self.state == State.WAITING_FOR_CLEAR:
            self._handle_waiting_for_clear()
    
    def _handle_baseline_capture(self):
        """Capture baseline frame."""
        frames = self.cameras.read_all()
        if frames and frames[0][1] is not None:
            # Use first camera for baseline (could average across cameras)
            self.detector.set_baseline(frames[0][1])
            self.dart_count = 0
            self._transition(State.WAITING_FOR_DART)
    
    def _handle_waiting_for_dart(self):
        """Watch for dart to hit board."""
        frames = self.cameras.read_all()
        if not frames:
            return
        
        # Check first camera for motion (could check all)
        _, frame = frames[0]
        if frame is None:
            return
        
        result = self.detector.compare_to_baseline(frame)
        
        if result.has_change and self.detector.can_detect():
            logger.info(f"Motion detected! Diff: {result.diff_percentage:.2f}%, Area: {result.largest_contour_area}")
            self._settling_start = time.time() * 1000
            self._transition(State.SETTLING)
    
    def _handle_settling(self):
        """Wait for dart to stop moving."""
        now = time.time() * 1000
        elapsed = now - self._settling_start
        
        if elapsed >= self.detector.config.settling_ms:
            self._transition(State.SENDING)
    
    def _handle_sending(self):
        """Capture and send snapshot to server."""
        frames = self.cameras.read_all()
        
        # Send to server
        if len(frames) == 1:
            camera_id, frame = frames[0]
            if frame is not None:
                response = self.client.detect(camera_id, frame)
        else:
            response = self.client.detect_multi(frames)
        
        if response.success:
            logger.info(f"Detection response: {response.darts}")
            self.client.notify_dart_detected(self.dart_count)
            
            # UPDATE BASELINE to include the new dart - prevents recounting!
            # Capture current frame (with dart in place) as new baseline
            new_frames = self.cameras.read_all()
            if new_frames and new_frames[0][1] is not None:
                self.detector.set_baseline(new_frames[0][1])
                logger.info("Baseline updated to include dart %d", self.dart_count + 1)
        else:
            logger.error(f"Detection failed: {response.error}")
        
        self.dart_count += 1
        self.detector.mark_detection()
        
        # Check if turn is complete
        if self.dart_count >= self.max_darts:
            self._transition(State.WAITING_FOR_CLEAR)
        else:
            self._transition(State.WAITING_FOR_DART)
    
    def _handle_waiting_for_clear(self):
        """Wait for darts to be removed from board."""
        frames = self.cameras.read_all()
        if not frames:
            return
        
        _, frame = frames[0]
        if frame is None:
            return
        
        if self.detector.matches_baseline(frame):
            logger.info("Board cleared - turn complete")
            self.client.notify_board_clear()
            
            # Update baseline if configured
            if self.detector.config.baseline_update_on_clear:
                self.detector.set_baseline(frame)
            
            self.dart_count = 0
            self._transition(State.WAITING_FOR_DART)
    
    def set_max_darts(self, count: int):
        """Set maximum darts per turn (for game logic)."""
        self.max_darts = count
    
    def reset_turn(self):
        """Reset for a new turn (external trigger)."""
        self.dart_count = 0
        self._transition(State.CAPTURING_BASELINE)


def main():
    """Entry point."""
    import argparse
    
    parser = argparse.ArgumentParser(description="DartSensor - Dart detection client")
    parser.add_argument("-c", "--config", default="config/settings.yaml",
                        help="Path to configuration file")
    args = parser.parse_args()
    
    sensor = DartSensor(args.config)
    
    # Handle signals
    def signal_handler(sig, frame):
        sensor.stop()
        sys.exit(0)
    
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)
    
    sensor.start()


if __name__ == "__main__":
    main()
