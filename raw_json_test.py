import sys, ctypes, json, os, base64
sys.stdout.reconfigure(encoding='utf-8')

# Call dd_detect directly and print raw JSON
import requests
game_id = "ede9eb00-367d-4a87-8b18-64dba8a49710"
base = f"C:\\Users\\clawd\\DartBenchmark\\A3C8DCD1-4196-4BF6-BD20-50310B960745\\{game_id}"
dart_dir = os.path.join(base, "round_1_Player 1", "dart_1")

images = []
before_images = []
for cam_id in ["cam0", "cam1", "cam2"]:
    for ext in [".jpg", ".png"]:
        raw = os.path.join(dart_dir, f"{cam_id}_raw{ext}")
        prev = os.path.join(dart_dir, f"{cam_id}_previous{ext}")
        if os.path.exists(raw):
            with open(raw, "rb") as f:
                images.append({"cameraId": cam_id, "image": base64.b64encode(f.read()).decode()})
            with open(prev, "rb") as f:
                before_images.append({"cameraId": cam_id, "image": base64.b64encode(f.read()).decode()})
            break

payload = {"boardId": "default", "images": images, "beforeImages": before_images, "requestId": "raw_test"}
r = requests.post("http://127.0.0.1:5000/api/games/benchmark/detect", json=payload, timeout=30)
d = r.json()

# Print the debugLines section raw
print("debugLines key present:", "debugLines" in d)
dl = d.get("debugLines")
if dl:
    # Print first camera raw
    first_cam = list(dl.keys())[0]
    print(f"\n{first_cam} raw keys: {list(dl[first_cam].keys())}")
    print(json.dumps(dl[first_cam], indent=2))
else:
    print("debugLines is None/missing")
    print("All keys:", list(d.keys()))
    print("Raw response (first 1000 chars):", json.dumps(d)[:1000])
