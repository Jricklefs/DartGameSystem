"""Quick camera test - just cameras 0 and 2."""
import cv2
import os

OUTPUT_DIR = "C:/Users/Clawd/DartSensor/test_output"
os.makedirs(OUTPUT_DIR, exist_ok=True)

# Only test 0 and 2 (1 seems problematic)
for cam_id in [0, 2]:
    print(f"Testing camera {cam_id}...", flush=True)
    cap = cv2.VideoCapture(cam_id, cv2.CAP_DSHOW)  # Use DirectShow backend
    
    if not cap.isOpened():
        print(f"  Camera {cam_id}: FAILED")
        continue
    
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    
    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    
    ret, frame = cap.read()
    cap.release()
    
    if ret and frame is not None:
        filename = f"{OUTPUT_DIR}/cam{cam_id}.jpg"
        cv2.imwrite(filename, frame)
        print(f"  Camera {cam_id}: OK - {w}x{h} - saved {filename}")
    else:
        print(f"  Camera {cam_id}: read failed")

print("Done!")
