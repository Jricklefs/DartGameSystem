"""
HTTP client for DartDetectionAI server.
"""

import requests
import logging
import base64
import cv2
import numpy as np
from typing import Optional, Dict, Any, List
from dataclasses import dataclass

logger = logging.getLogger(__name__)


@dataclass
class ServerConfig:
    url: str
    api_key: str = ""
    timeout_ms: int = 5000


@dataclass
class DetectionResponse:
    """Response from /detect endpoint."""
    success: bool
    darts: List[Dict[str, Any]]  # [{x, y, score, segment, multiplier}, ...]
    error: Optional[str] = None


class DetectionClient:
    """Client for communicating with DartDetectionAI server."""
    
    def __init__(self, config: ServerConfig, board_id: str):
        self.config = config
        self.board_id = board_id
        self.session = requests.Session()
        
        # Set up headers
        if config.api_key:
            self.session.headers["Authorization"] = f"Bearer {config.api_key}"
        self.session.headers["Content-Type"] = "application/json"
    
    def _url(self, path: str) -> str:
        """Build full URL from path."""
        return f"{self.config.url.rstrip('/')}/{path.lstrip('/')}"
    
    def _encode_image(self, frame: np.ndarray) -> str:
        """Encode frame as base64 JPEG."""
        _, buffer = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 90])
        return base64.b64encode(buffer).decode('utf-8')
    
    def detect(self, camera_id: str, frame: np.ndarray) -> DetectionResponse:
        """
        Send frame to server for dart tip detection.
        
        Args:
            camera_id: ID of the camera that captured the frame
            frame: The captured frame (BGR numpy array)
            
        Returns:
            DetectionResponse with dart positions and scores
        """
        try:
            payload = {
                "board_id": self.board_id,
                "camera_id": camera_id,
                "image": self._encode_image(frame)
            }
            
            resp = self.session.post(
                self._url("/detect"),
                json=payload,
                timeout=self.config.timeout_ms / 1000
            )
            resp.raise_for_status()
            
            data = resp.json()
            return DetectionResponse(
                success=True,
                darts=data.get("darts", [])
            )
            
        except requests.exceptions.Timeout:
            logger.error("Detection request timed out")
            return DetectionResponse(success=False, darts=[], error="timeout")
        except requests.exceptions.RequestException as e:
            logger.error(f"Detection request failed: {e}")
            return DetectionResponse(success=False, darts=[], error=str(e))
    
    def detect_multi(self, frames: List[tuple]) -> DetectionResponse:
        """
        Send frames from multiple cameras for detection.
        
        Args:
            frames: List of (camera_id, frame) tuples
            
        Returns:
            DetectionResponse with dart positions and scores
        """
        try:
            images = []
            for camera_id, frame in frames:
                if frame is not None:
                    images.append({
                        "camera_id": camera_id,
                        "image": self._encode_image(frame)
                    })
            
            payload = {
                "board_id": self.board_id,
                "images": images
            }
            
            resp = self.session.post(
                self._url("/detect/multi"),
                json=payload,
                timeout=self.config.timeout_ms / 1000
            )
            resp.raise_for_status()
            
            data = resp.json()
            return DetectionResponse(
                success=True,
                darts=data.get("darts", [])
            )
            
        except requests.exceptions.RequestException as e:
            logger.error(f"Multi-camera detection request failed: {e}")
            return DetectionResponse(success=False, darts=[], error=str(e))
    
    def notify_dart_detected(self, dart_index: int):
        """Notify server that a dart was detected (for event tracking)."""
        try:
            self.session.post(
                self._url("/events/dart"),
                json={"board_id": self.board_id, "dart_index": dart_index},
                timeout=2
            )
        except Exception as e:
            logger.warning(f"Failed to notify dart event: {e}")
    
    def notify_board_clear(self):
        """Notify server that board is clear (turn complete)."""
        try:
            self.session.post(
                self._url("/events/clear"),
                json={"board_id": self.board_id},
                timeout=2
            )
        except Exception as e:
            logger.warning(f"Failed to notify clear event: {e}")
    
    def health_check(self) -> bool:
        """Check if server is reachable."""
        try:
            resp = self.session.get(
                self._url("/health"),
                timeout=2
            )
            return resp.status_code == 200
        except Exception:
            return False
