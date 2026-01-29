"""
Camera capture abstraction for DartSensor.
Supports USB cameras and Pi camera module.
"""

import cv2
import numpy as np
import logging
from typing import Optional, Tuple, List
from dataclasses import dataclass

logger = logging.getLogger(__name__)


@dataclass
class CameraConfig:
    id: str
    device: int
    width: int = 1280
    height: int = 720
    fps: int = 30


class Camera:
    """Single camera wrapper."""
    
    def __init__(self, config: CameraConfig):
        self.config = config
        self.cap: Optional[cv2.VideoCapture] = None
        self._is_open = False
    
    def open(self) -> bool:
        """Open the camera device."""
        try:
            self.cap = cv2.VideoCapture(self.config.device)
            if not self.cap.isOpened():
                logger.error(f"Failed to open camera {self.config.id} (device {self.config.device})")
                return False
            
            # Set resolution and FPS
            self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.config.width)
            self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.config.height)
            self.cap.set(cv2.CAP_PROP_FPS, self.config.fps)
            
            # Verify settings
            actual_w = int(self.cap.get(cv2.CAP_PROP_FRAME_WIDTH))
            actual_h = int(self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
            actual_fps = int(self.cap.get(cv2.CAP_PROP_FPS))
            
            logger.info(f"Camera {self.config.id} opened: {actual_w}x{actual_h} @ {actual_fps}fps")
            self._is_open = True
            return True
            
        except Exception as e:
            logger.error(f"Error opening camera {self.config.id}: {e}")
            return False
    
    def read(self) -> Tuple[bool, Optional[np.ndarray]]:
        """Read a frame from the camera."""
        if not self._is_open or self.cap is None:
            return False, None
        return self.cap.read()
    
    def close(self):
        """Release the camera."""
        if self.cap is not None:
            self.cap.release()
            self._is_open = False
            logger.info(f"Camera {self.config.id} closed")
    
    @property
    def is_open(self) -> bool:
        return self._is_open


class CameraManager:
    """Manages multiple cameras."""
    
    def __init__(self, configs: List[CameraConfig]):
        self.cameras = [Camera(cfg) for cfg in configs]
    
    def open_all(self) -> bool:
        """Open all cameras. Returns True if all succeed."""
        success = True
        for cam in self.cameras:
            if not cam.open():
                success = False
        return success
    
    def read_all(self) -> List[Tuple[str, Optional[np.ndarray]]]:
        """Read from all cameras. Returns list of (camera_id, frame) tuples."""
        results = []
        for cam in self.cameras:
            ret, frame = cam.read()
            if ret:
                results.append((cam.config.id, frame))
            else:
                results.append((cam.config.id, None))
                logger.warning(f"Failed to read from camera {cam.config.id}")
        return results
    
    def close_all(self):
        """Close all cameras."""
        for cam in self.cameras:
            cam.close()
    
    def get_camera(self, camera_id: str) -> Optional[Camera]:
        """Get a camera by ID."""
        for cam in self.cameras:
            if cam.config.id == camera_id:
                return cam
        return None
    
    @property
    def camera_ids(self) -> List[str]:
        return [cam.config.id for cam in self.cameras]
