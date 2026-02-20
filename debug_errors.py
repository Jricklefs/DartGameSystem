import requests, json, os, base64, sys
sys.stdout.reconfigure(encoding='utf-8')

game_id = "35c59e73-3a2f-485f-8b39-ed23cfdf1b7f"
base = f"C:\\Users\\clawd\\DartBenchmark\\A3C8DCD1-4196-4BF6-BD20-50310B960745\\{game_id}"

errors = [
    ("round_1_Player 1", "dart_1", "S1→T1"),
    ("round_5_Player 1", "dart_2", "S20→T20"),
    ("round_7_Player 1", "dart_1", "T12→S12"),
    ("round_9_Player 1", "dart_3", "S20→D20"),
]

for round_name, dart_name, desc in errors:
    dart_dir = os.path.join(base, round_name, dart_name)
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

    payload = {"boardId": "default", "images": images, "beforeImages": before_images, "requestId": "debug"}
    r = requests.post("http://localhost:5000/api/games/benchmark/detect", json=payload, timeout=30)
    d = r.json()
    
    darts = d.get("darts", [])
    dart_info = darts[0] if darts else {}
    dl = d.get("debugLines") or d.get("debug_lines") or {}
    method = d.get("method", "N/A")
    
    print(f"\n{'='*60}")
    print(f"{round_name}/{dart_name}: {desc}")
    print(f"  Result: S{dart_info.get('segment','?')}x{dart_info.get('multiplier','?')} ({dart_info.get('zone','?')})")
    print(f"  Method: {method}")
    print(f"  Coords: ({d.get('coordsX','?')}, {d.get('coordsY','?')})")
    print(f"  Debug lines: {len(dl)} cameras")
    if dl:
        for cam, info in dl.items():
            # Handle both snake_case (C++ direct) and camelCase (C# serialized)
            lsx = info.get('ls_x') or info.get('lsX', 0)
            lsy = info.get('ls_y') or info.get('lsY', 0)
            lex = info.get('le_x') or info.get('leX', 0)
            ley = info.get('le_y') or info.get('leY', 0)
            td = info.get('tip_dist') or info.get('tipDist', 0)
            mq = info.get('mask_q') or info.get('maskQ', 0)
            rel = info.get('tip_reliable') or info.get('tipReliable', False)
            print(f"    {cam}: line ({lsx:.3f},{lsy:.3f})->({lex:.3f},{ley:.3f}) tip_d={td:.3f} mq={mq:.0f} rel={rel}")
    
    # Check per-camera votes
    pvotes = d.get("perCameraVotes", d.get("per_camera_votes", {}))
    if pvotes:
        print(f"  Per-camera votes: {pvotes}")
