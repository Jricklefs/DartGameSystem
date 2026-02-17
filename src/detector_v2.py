"""
DartSensor Detector v2 - Multi-Camera
Dual-comparison logic with drift compensation and multi-camera consensus.
Borrows hand-detection and clearing logic from DartDetector.
"""

import cv2
import numpy as np
import time
import logging
from typing import Optional, Tuple, List, Dict
from dataclasses import dataclass, field
from enum import Enum, auto

logger = logging.getLogger(__name__)


class BoardState(Enum):
    """Current state of the dartboard."""
    CLEAR = auto()           # No darts, matches baseline
    HAS_DARTS = auto()       # Darts on board, stable
    NEW_DART = auto()        # Just detected a new dart
    BOARD_CLEARED = auto()   # Darts were just removed
    HAND_IN_FRAME = auto()   # Hand/arm blocking camera
    CLEARING = auto()        # Darts being removed (in progress)


@dataclass
class DetectorConfig:
    """Configuration for the detector."""
    # Thresholds (scaled 20x for readability - dart shows ~15-25%)
    base_threshold_pct: float = 5.0       # % diff to consider "changed" from base (was 10.0)
    dart_threshold_pct: float = 4.0       # % diff to detect new dart vs Image1 (was 4.0, then 8.0)
    clear_threshold_pct: float = 3.0      # % diff to consider "matches baseline" (was 5.0)
    
    # Hand/clearing detection (from DartDetector, scaled)
    hand_threshold_pct: float = 300.0     # % diff = hand blocking camera (very high)
    clearing_start_pct: float = 200.0     # % diff = might be pulling darts (was 150.0)
    clearing_finish_pct: float = 280.0    # % diff = definitely pulling darts (was 250.0)
    
    # Drift compensation
    enable_drift_correction: bool = True
    drift_blend_alpha: float = 0.05       # How much to blend (0.05 = 5% new frame)
    idle_refresh_seconds: float = 60.0     # Hard refresh baseline after this idle time
    
    # Motion detection resolution
    # Autodarts uses separate resolutions: detectionResolution=1280x720, motionResolution=320x180
    # Lower motion resolution = less noise in diff, faster processing.
    # The full-res frames are still sent to DartDetect for actual scoring.
    # Set to None to use full capture resolution for motion detection.
    motion_width: Optional[int] = 640
    motion_height: Optional[int] = 360
    
    # Noise reduction
    blur_kernel_size: int = 5             # Gaussian blur kernel (odd number)
    min_contour_area: int = 500           # Minimum contour area to count as dart
    max_contour_area: int = 50000         # Maximum contour area
    
    # Timing
    settling_ms: int = 150                # Wait for dart to stop wobbling
    cooldown_ms: int = 300                # Minimum time between detections
    warmup_ms: int = 800                  # Initial warmup before detection (from DartDetector)
    
    # Multi-camera consensus
    # Minimum cameras that must agree on a state change (new dart, clearing, etc).
    # Set to 2 to require multi-camera consensus â€” prevents false triggers from
    # single-camera lighting flicker, electrical noise, or dust settling.
    # With 3 cameras, 2 is a safe majority. With 1 camera, falls back to 1.
    min_cameras_agree: int = 1
    
    # ROI (Region of Interest) - set via calibration per camera
    roi_center: Optional[Tuple[int, int]] = None
    roi_radius: Optional[int] = None


@dataclass
class CameraState:
    """State for a single camera."""
    camera_id: str
    base_image: Optional[np.ndarray] = None      # Preprocessed baseline
    base_image_raw: Optional[np.ndarray] = None  # Raw baseline for display
    image1: Optional[np.ndarray] = None          # Preprocessed current state
    image1_raw: Optional[np.ndarray] = None      # Raw image1 for display
    roi_mask: Optional[np.ndarray] = None
    last_diff_base: float = 0.0
    last_diff_img1: float = 0.0
    noise_floor: float = 0.5
    noise_samples: List[float] = field(default_factory=list)


@dataclass
class DetectionResult:
    """Result of a detection check."""
    state: BoardState
    cameras_detecting_change: int
    cameras_matching_base: int
    camera_results: Dict[str, dict]  # Per-camera details
    centroid: Optional[Tuple[int, int]] = None
    message: str = ""


class MultiCameraDartDetector:
    """
    Multi-camera dart detector with consensus voting.
    
    Each camera has its own baseline and Image1.
    Detection requires agreement from multiple cameras.
    """
    
    def __init__(self, config: Optional[DetectorConfig] = None):
        self.config = config or DetectorConfig()
        
        # Per-camera state
        self.cameras: Dict[str, CameraState] = {}
        
        # Timing
        self._last_detection_time: float = 0
        self._board_clear_since: Optional[float] = None
        
        # Drift control
        self.pause_drift: bool = False  # Pause drift correction during settling
        
        # Stats
        self.dart_count: int = 0
    
    def add_camera(self, camera_id: str):
        """Register a camera for tracking."""
        self.cameras[camera_id] = CameraState(camera_id=camera_id)
        logger.info(f"Added camera: {camera_id}")
    
    def _downscale_for_motion(self, frame: np.ndarray) -> np.ndarray:
        """
        Downscale frame to motion detection resolution.
        
        Motion detection only needs to know IF something changed, not exactly where.
        Lower resolution = less noise, fewer false positives from pixel-level jitter.
        This mirrors Autodarts' approach: motionResolution=320x180, detectionResolution=1280x720.
        
        Full-res frames are still sent to DartDetect for actual tip detection/scoring.
        """
        mw = self.config.motion_width
        mh = self.config.motion_height
        if mw and mh and (frame.shape[1] != mw or frame.shape[0] != mh):
            return cv2.resize(frame, (mw, mh), interpolation=cv2.INTER_AREA)
        return frame
    
    def _preprocess(self, frame: np.ndarray) -> np.ndarray:
        """Convert to grayscale and blur for motion comparison.
        
        Downscales to motion resolution first (if configured), then converts
        to grayscale and blurs. This reduces noise for motion detection while
        keeping full-res frames available for DartDetect.
        """
        # Downscale to motion resolution for cleaner diffs
        frame = self._downscale_for_motion(frame)
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        blurred = cv2.GaussianBlur(
            gray, 
            (self.config.blur_kernel_size, self.config.blur_kernel_size), 
            0
        )
        return blurred
    
    def _create_roi_mask(self, shape: Tuple[int, int]) -> np.ndarray:
        """Create circular ROI mask if configured."""
        if self.config.roi_center is None or self.config.roi_radius is None:
            return np.ones(shape, dtype=np.uint8) * 255
        
        mask = np.zeros(shape, dtype=np.uint8)
        cv2.circle(mask, self.config.roi_center, self.config.roi_radius, 255, -1)
        return mask
    
    def _calculate_diff(
        self, 
        frame: np.ndarray, 
        reference: np.ndarray,
        roi_mask: Optional[np.ndarray] = None
    ) -> Tuple[float, np.ndarray, List[np.ndarray]]:
        """
        Calculate difference between frame and reference.
        
        Returns scaled percentage for easier threshold tuning.
        A single dart typically shows as 10-25% instead of 0.5-1.5%.
        """
        diff = cv2.absdiff(frame, reference)
        
        if roi_mask is not None:
            diff = cv2.bitwise_and(diff, roi_mask)
        
        _, thresh = cv2.threshold(diff, 25, 255, cv2.THRESH_BINARY)
        
        if roi_mask is not None:
            total_pixels = cv2.countNonZero(roi_mask)
        else:
            total_pixels = diff.shape[0] * diff.shape[1]
        
        changed_pixels = cv2.countNonZero(thresh)
        
        # Raw percentage
        raw_pct = (changed_pixels / total_pixels) * 100 if total_pixels > 0 else 0
        
        # Scale up for human-readable values (dart = ~15-25% instead of ~1%)
        # Using 20x multiplier so thresholds are more intuitive
        diff_pct = raw_pct * 20.0
        
        contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        # Scale contour area thresholds to motion resolution.
        # Config thresholds are defined for 1280x720. If we're running at 640x360,
        # areas are 1/4 as large (linear dimensions halved â†’ area quartered).
        scale = 1.0
        if self.config.motion_width and self.config.motion_height:
            # Reference resolution: 1280x720 (what the config thresholds assume)
            scale = (self.config.motion_width * self.config.motion_height) / (1280 * 720)
        min_area = self.config.min_contour_area * scale
        max_area = self.config.max_contour_area * scale
        
        valid_contours = [
            c for c in contours 
            if min_area <= cv2.contourArea(c) <= max_area
        ]
        
        return diff_pct, diff, valid_contours
    
    def _blend_image(self, current: np.ndarray, new_frame: np.ndarray) -> np.ndarray:
        """Blend new frame into current for drift correction."""
        alpha = self.config.drift_blend_alpha
        return cv2.addWeighted(current, 1 - alpha, new_frame, alpha, 0)
    
    def set_baseline(self, camera_id: str, frame: np.ndarray):
        """Set baseline for a specific camera."""
        if camera_id not in self.cameras:
            self.add_camera(camera_id)
        
        cam = self.cameras[camera_id]
        cam.base_image_raw = frame.copy()
        cam.base_image = self._preprocess(frame)
        cam.image1 = None
        cam.image1_raw = None
        cam.roi_mask = self._create_roi_mask(cam.base_image.shape)
        cam.noise_samples = []
        logger.info(f"Baseline set for camera {camera_id}")
    
    def set_all_baselines(self, frames: Dict[str, np.ndarray]):
        """Set baselines for all cameras at once."""
        for camera_id, frame in frames.items():
            self.set_baseline(camera_id, frame)
        self._board_clear_since = time.time()
        self.dart_count = 0
    
    def set_image1(self, camera_id: str, frame: np.ndarray):
        """Set Image1 for a specific camera."""
        if camera_id not in self.cameras:
            return
        
        cam = self.cameras[camera_id]
        cam.image1_raw = frame.copy()
        cam.image1 = self._preprocess(frame)
        logger.info(f"Image1 set for camera {camera_id}")
    
    def set_all_image1(self, frames: Dict[str, np.ndarray]):
        """Set Image1 for all cameras at once."""
        for camera_id, frame in frames.items():
            self.set_image1(camera_id, frame)
        self._board_clear_since = None
    
    def clear_all_image1(self):
        """Clear Image1 for all cameras."""
        for cam in self.cameras.values():
            cam.image1 = None
            cam.image1_raw = None
        self._board_clear_since = time.time()
        self.dart_count = 0
        logger.info("All Image1 cleared")
    
    def can_detect(self) -> bool:
        """Check if enough time has passed since last detection."""
        now = time.time() * 1000
        return (now - self._last_detection_time) >= self.config.cooldown_ms
    
    def mark_detection(self):
        """Mark that a detection just occurred."""
        self._last_detection_time = time.time() * 1000
    
    def process_frames(self, frames: Dict[str, np.ndarray]) -> DetectionResult:
        """
        Process frames from all cameras and determine board state.
        
        Args:
            frames: Dict of camera_id -> frame
            
        Returns:
            DetectionResult with consensus state
        """
        now = time.time()
        camera_results = {}
        
        cameras_detecting_change = 0
        cameras_matching_base = 0
        cameras_with_new_dart = 0
        cameras_with_hand = 0
        cameras_clearing = 0
        
        all_contours = []
        
        for camera_id, frame in frames.items():
            if camera_id not in self.cameras:
                self.add_camera(camera_id)
            
            cam = self.cameras[camera_id]
            
            if cam.base_image is None:
                camera_results[camera_id] = {
                    'state': 'no_baseline',
                    'diff_base': 0,
                    'diff_img1': 0
                }
                continue
            
            processed = self._preprocess(frame)
            
            # Compare to baseline
            diff_base_pct, _, contours_base = self._calculate_diff(
                processed, cam.base_image, cam.roi_mask
            )
            cam.last_diff_base = diff_base_pct
            
            # Compare to Image1
            if cam.image1 is not None:
                diff_img1_pct, _, contours_img1 = self._calculate_diff(
                    processed, cam.image1, cam.roi_mask
                )
            else:
                diff_img1_pct = 100.0
                contours_img1 = contours_base
            cam.last_diff_img1 = diff_img1_pct
            
            # Check for hand in frame (massive change)
            if diff_base_pct > self.config.hand_threshold_pct:
                cameras_with_hand += 1
                state = 'hand'
                camera_results[camera_id] = {
                    'state': state,
                    'diff_base': diff_base_pct,
                    'diff_img1': diff_img1_pct,
                    'contours': 0
                }
                continue
            
            # Check for clearing in progress (medium-high change, likely arm reaching in)
            if diff_base_pct > self.config.clearing_start_pct:
                cameras_clearing += 1
                state = 'clearing'
                camera_results[camera_id] = {
                    'state': state,
                    'diff_base': diff_base_pct,
                    'diff_img1': diff_img1_pct,
                    'contours': 0
                }
                continue
            
            # Normal evaluation
            matches_base = diff_base_pct < self.config.clear_threshold_pct
            has_change_from_base = diff_base_pct > self.config.base_threshold_pct
            has_change_from_img1 = diff_img1_pct > self.config.dart_threshold_pct
            
            # Log notable diffs for debugging missed detections (DISABLED)
            # if diff_img1_pct > 1.0 or diff_base_pct > 2.0:
            #     logger.info(f"[DIFF] ...")
            
            if matches_base:
                cameras_matching_base += 1
                state = 'clear'
                # Drift correction on baseline
                if self.config.enable_drift_correction and cam.image1 is None and not self.pause_drift:
                    cam.base_image = self._blend_image(cam.base_image, processed)
            elif cam.image1 is not None and has_change_from_img1:
                cameras_detecting_change += 1
                cameras_with_new_dart += 1
                state = 'new_dart'
                all_contours.extend(contours_img1)
            elif cam.image1 is None and has_change_from_base:
                cameras_detecting_change += 1
                cameras_with_new_dart += 1
                state = 'new_dart'
                all_contours.extend(contours_base)
            else:
                state = 'stable'
                # Drift correction on Image1
                if self.config.enable_drift_correction and cam.image1 is not None and not self.pause_drift:
                    cam.image1 = self._blend_image(cam.image1, processed)
            
            camera_results[camera_id] = {
                'state': state,
                'diff_base': diff_base_pct,
                'diff_img1': diff_img1_pct,
                'contours': len(contours_base)
            }
        
        total_cameras = len(frames)
        
        # Hand in frame: any camera blocked = wait
        if cameras_with_hand > 0:
            return DetectionResult(
                state=BoardState.HAND_IN_FRAME,
                cameras_detecting_change=cameras_detecting_change,
                cameras_matching_base=cameras_matching_base,
                camera_results=camera_results,
                message=f"Hand in frame ({cameras_with_hand} camera(s))"
            )
        
        # Clearing in progress: multiple cameras see medium-high change
        if cameras_clearing >= self.config.min_cameras_agree:
            return DetectionResult(
                state=BoardState.CLEARING,
                cameras_detecting_change=cameras_detecting_change,
                cameras_matching_base=cameras_matching_base,
                camera_results=camera_results,
                message=f"Clearing darts ({cameras_clearing} camera(s))"
            )
        
        # Board cleared: majority match baseline
        if cameras_matching_base >= total_cameras // 2 + 1:
            had_darts = any(cam.image1 is not None for cam in self.cameras.values())
            if had_darts:
                self.clear_all_image1()
                return DetectionResult(
                    state=BoardState.BOARD_CLEARED,
                    cameras_detecting_change=cameras_detecting_change,
                    cameras_matching_base=cameras_matching_base,
                    camera_results=camera_results,
                    message="Board cleared - consensus"
                )
            
            # Idle refresh REMOVED - rebase only at end of turn via SignalR
            # if (self._board_clear_since and 
            # now - self._board_clear_since > self.config.idle_refresh_seconds):
            # self.set_all_baselines(frames)
            # return DetectionResult(
            # state=BoardState.CLEAR,
            # cameras_detecting_change=0,
            # cameras_matching_base=total_cameras,
            # camera_results=camera_results,
            # message="Baselines refreshed (idle)"
            # )
            
            return DetectionResult(
                state=BoardState.CLEAR,
                cameras_detecting_change=0,
                cameras_matching_base=cameras_matching_base,
                camera_results=camera_results,
                message="Board clear"
            )
        
        # New dart: enough cameras agree
        if cameras_with_new_dart > 0:
            logger.info(f"[VOTE] new_dart={cameras_with_new_dart}/{total_cameras} "
                       f"clear={cameras_matching_base} hand={cameras_with_hand} "
                       f"clearing={cameras_clearing} need={self.config.min_cameras_agree}")
        if cameras_with_new_dart >= self.config.min_cameras_agree:
            if self.can_detect():
                centroid = self._get_centroid(all_contours) if all_contours else None
                return DetectionResult(
                    state=BoardState.NEW_DART,
                    cameras_detecting_change=cameras_detecting_change,
                    cameras_matching_base=cameras_matching_base,
                    camera_results=camera_results,
                    centroid=centroid,
                    message=f"New dart! ({cameras_with_new_dart}/{total_cameras} cameras agree)"
                )
        
        # Outlier re-sync REMOVED â€” was absorbing valid dart detections when only
        # 1 camera crossed the threshold. Single-camera detections should be ignored
        # (not detected), NOT used to re-sync baselines/Image1.
        
        # Default: has darts, stable
        return DetectionResult(
            state=BoardState.HAS_DARTS,
            cameras_detecting_change=cameras_detecting_change,
            cameras_matching_base=cameras_matching_base,
            camera_results=camera_results,
            message=f"Stable ({self.dart_count} darts)"
        )
    
    def _get_centroid(self, contours: List[np.ndarray]) -> Optional[Tuple[int, int]]:
        """Get centroid of the largest contour."""
        if not contours:
            return None
        
        largest = max(contours, key=cv2.contourArea)
        M = cv2.moments(largest)
        if M["m00"] > 0:
            cx = int(M["m10"] / M["m00"])
            cy = int(M["m01"] / M["m00"])
            return (cx, cy)
        return None
    
    def get_debug_frame(self, camera_id: str, frame: np.ndarray) -> np.ndarray:
        """Generate debug visualization for a camera."""
        debug = frame.copy()
        
        if camera_id not in self.cameras:
            cv2.putText(debug, "NO CAMERA", (10, 30),
                       cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 0, 255), 2)
            return debug
        
        cam = self.cameras[camera_id]
        
        if cam.base_image is None:
            cv2.putText(debug, "NO BASELINE", (10, 30),
                       cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 0, 255), 2)
            return debug
        
        # Draw ROI
        if self.config.roi_center and self.config.roi_radius:
            cv2.circle(debug, self.config.roi_center, self.config.roi_radius, 
                      (255, 255, 0), 2)
        
        # Info overlay
        y = 30
        lines = [
            f"Cam: {camera_id}",
            f"Diff Base: {cam.last_diff_base:.2f}%",
            f"Diff Img1: {cam.last_diff_img1:.2f}%",
            f"Has Img1: {'Y' if cam.image1 is not None else 'N'}",
        ]
        
        for line in lines:
            cv2.putText(debug, line, (10, y),
                       cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1)
            y += 20
        
        return debug
    
    def get_camera_state(self, camera_id: str) -> Optional[CameraState]:
        """Get state for a specific camera."""
        return self.cameras.get(camera_id)


# Keep the single-camera version for compatibility
class DartDetectorV2(MultiCameraDartDetector):
    """Single-camera wrapper for backwards compatibility."""
    
    def __init__(self, config: Optional[DetectorConfig] = None):
        super().__init__(config)
        self._default_camera = "cam0"
        self.add_camera(self._default_camera)
    
    def set_baseline(self, frame: np.ndarray):
        """Set baseline for default camera."""
        super().set_baseline(self._default_camera, frame)
        self._board_clear_since = time.time()
        self.dart_count = 0
    
    def set_image1(self, frame: np.ndarray):
        """Set Image1 for default camera."""
        super().set_image1(self._default_camera, frame)
        self._board_clear_since = None
    
    def clear_image1(self):
        """Clear Image1 for default camera."""
        cam = self.cameras[self._default_camera]
        cam.image1 = None
        cam.image1_raw = None
        self._board_clear_since = time.time()
        self.dart_count = 0
    
    def process_frame(self, frame: np.ndarray) -> DetectionResult:
        """Process single frame."""
        return self.process_frames({self._default_camera: frame})
    
    def get_debug_frame(self, frame: np.ndarray) -> np.ndarray:
        """Generate debug frame for default camera."""
        return super().get_debug_frame(self._default_camera, frame)
    
    @property
    def base_image(self) -> Optional[np.ndarray]:
        cam = self.cameras.get(self._default_camera)
        return cam.base_image_raw if cam else None
    
    @property
    def image1(self) -> Optional[np.ndarray]:
        cam = self.cameras.get(self._default_camera)
        return cam.image1_raw if cam else None



