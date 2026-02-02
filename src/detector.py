"""
Frame difference detector for DartSensor.
Detects darts by comparing current frame to baseline.
"""

import cv2
import numpy as np
import logging
import time
from typing import Optional, Tuple, List
from dataclasses import dataclass

logger = logging.getLogger(__name__)


@dataclass
class DetectionConfig:
    diff_threshold: int = 25
    min_contour_area: int = 500
    max_contour_area: int = 50000
    settling_ms: int = 150
    cooldown_ms: int = 500
    baseline_match_pct: float = 97.0
    baseline_update_on_clear: bool = True


@dataclass
class DetectionResult:
    """Result of a detection check."""
    has_change: bool
    diff_percentage: float
    contours: List[np.ndarray]
    largest_contour_area: int
    centroid: Optional[Tuple[int, int]] = None


class FrameDetector:
    """Detects changes between frames using baseline comparison."""
    
    def __init__(self, config: DetectionConfig):
        self.config = config
        self.baseline: Optional[np.ndarray] = None
        self.last_detection_time: float = 0
        self._settling_start: Optional[float] = None
        self._last_diff_pct: float = 0
    
    def set_baseline(self, frame: np.ndarray):
        """Set the baseline frame (clean board, no darts)."""
        self.baseline = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        self.baseline = cv2.GaussianBlur(self.baseline, (5, 5), 0)
        logger.info("Baseline frame set")
    
    def compare_to_baseline(self, frame: np.ndarray) -> DetectionResult:
        """Compare a frame to baseline and detect changes."""
        if self.baseline is None:
            return DetectionResult(
                has_change=False,
                diff_percentage=0,
                contours=[],
                largest_contour_area=0
            )
        
        # Convert to grayscale and blur
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        gray = cv2.GaussianBlur(gray, (5, 5), 0)
        
        # Compute absolute difference
        diff = cv2.absdiff(self.baseline, gray)
        
        # Threshold the difference
        _, thresh = cv2.threshold(diff, self.config.diff_threshold, 255, cv2.THRESH_BINARY)
        
        # Calculate diff percentage
        total_pixels = thresh.shape[0] * thresh.shape[1]
        changed_pixels = np.count_nonzero(thresh)
        diff_pct = (changed_pixels / total_pixels) * 100
        self._last_diff_pct = diff_pct
        
        # Find contours
        contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        # Filter contours by area
        valid_contours = []
        largest_area = 0
        largest_contour = None
        
        for cnt in contours:
            area = cv2.contourArea(cnt)
            if self.config.min_contour_area <= area <= self.config.max_contour_area:
                valid_contours.append(cnt)
                if area > largest_area:
                    largest_area = area
                    largest_contour = cnt
        
        # Calculate centroid of largest contour
        centroid = None
        if largest_contour is not None:
            M = cv2.moments(largest_contour)
            if M["m00"] > 0:
                cx = int(M["m10"] / M["m00"])
                cy = int(M["m01"] / M["m00"])
                centroid = (cx, cy)
        
        has_change = len(valid_contours) > 0
        
        return DetectionResult(
            has_change=has_change,
            diff_percentage=diff_pct,
            contours=valid_contours,
            largest_contour_area=largest_area,
            centroid=centroid
        )
    
    def matches_baseline(self, frame: np.ndarray) -> bool:
        """Check if frame matches baseline (board is clear)."""
        result = self.compare_to_baseline(frame)
        match_pct = 100 - result.diff_percentage
        return match_pct >= self.config.baseline_match_pct
    
    def can_detect(self) -> bool:
        """Check if enough time has passed since last detection (cooldown)."""
        now = time.time() * 1000
        return (now - self.last_detection_time) >= self.config.cooldown_ms
    
    def mark_detection(self):
        """Mark that a detection occurred (for cooldown)."""
        self.last_detection_time = time.time() * 1000
    
    def get_debug_image(self, frame: np.ndarray) -> np.ndarray:
        """Generate a debug visualization of the detection."""
        if self.baseline is None:
            return frame.copy()
        
        result = self.compare_to_baseline(frame)
        debug = frame.copy()
        
        # Draw contours
        cv2.drawContours(debug, result.contours, -1, (0, 255, 0), 2)
        
        # Draw centroid
        if result.centroid:
            cv2.circle(debug, result.centroid, 10, (0, 0, 255), -1)
        
        # Draw info text
        cv2.putText(debug, f"Diff: {result.diff_percentage:.2f}%", 
                    (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
        cv2.putText(debug, f"Contours: {len(result.contours)}", 
                    (10, 70), cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
        
        return debug
