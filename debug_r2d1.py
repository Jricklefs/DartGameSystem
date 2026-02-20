import requests, json, os, base64

game_id = "e202e039-0fb2-4a1a-91d2-d3c6de38501f"
base = f"C:\\Users\\clawd\\DartBenchmark\\A3C8DCD1-4196-4BF6-BD20-50310B960745\\{game_id}"
dart_dir = os.path.join(base, "round_2_Player 1", "dart_1")

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

payload = {"boardId": "default", "images": images, "beforeImages": before_images, "requestId": "debug_r2d1"}
r = requests.post("http://localhost:5000/api/games/benchmark/detect", json=payload, timeout=30)
d = r.json()

print(json.dumps(d, indent=2)[:3000])
