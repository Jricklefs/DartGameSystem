"""Standalone camera test for DartSensor."""
import cv2
import time
import os

OUTPUT_DIR = "C:/Users/Clawd/DartSensor/test_output"
os.makedirs(OUTPUT_DIR, exist_ok=True)

cameras = [0, 1, 2]
results = []

for cam_id in cameras:
    print(f"\nTesting camera {cam_id}...")
    cap = cv2.VideoCapture(cam_id)
    
    if not cap.isOpened():
        print(f"  Camera {cam_id}: FAILED to open")
        results.append((cam_id, False, None))
        continue
    
    # Set resolution
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    
    # Read actual settings
    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = int(cap.get(cv2.CAP_PROP_FPS))
    
    # Warm up camera (some need a few frames)
    for _ in range(5):
        cap.read()
        time.sleep(0.05)
    
    # Capture test frame
    ret, frame = cap.read()
    cap.release()
    
    if ret and frame is not None:
        filename = f"{OUTPUT_DIR}/cam{cam_id}_test.jpg"
        cv2.imwrite(filename, frame)
        print(f"  Camera {cam_id}: OK - {w}x{h} @ {fps}fps - saved to {filename}")
        results.append((cam_id, True, f"{w}x{h}"))
    else:
        print(f"  Camera {cam_id}: FAILED to read frame")
        results.append((cam_id, False, None))

print("\n" + "="*50)
print("SUMMARY:")
for cam_id, success, res in results:
    status = f"OK ({res})" if success else "FAILED"
    print(f"  Camera {cam_id}: {status}")
print("="*50)
