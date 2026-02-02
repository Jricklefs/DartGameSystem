"""Quick test to verify cameras work with DartSensor code."""
import sys
sys.path.insert(0, 'src')

from camera import CameraManager, CameraConfig

# Configure 3 cameras
configs = [
    CameraConfig(id="cam0", device=0, width=1280, height=720, fps=30),
    CameraConfig(id="cam1", device=1, width=1280, height=720, fps=30),
    CameraConfig(id="cam2", device=2, width=1280, height=720, fps=30),
]

print("Opening cameras...")
manager = CameraManager(configs)
if not manager.open_all():
    print("Failed to open all cameras")
    sys.exit(1)

print("Reading frames...")
frames = manager.read_all()
for cam_id, frame in frames:
    if frame is not None:
        print(f"  {cam_id}: {frame.shape}")
    else:
        print(f"  {cam_id}: FAILED")

# Save a test frame from each
import cv2
for cam_id, frame in frames:
    if frame is not None:
        cv2.imwrite(f"test_{cam_id}.jpg", frame)
        print(f"Saved test_{cam_id}.jpg")

manager.close_all()
print("Done!")
