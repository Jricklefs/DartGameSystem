import requests, json, os, base64, sys, glob
sys.stdout.reconfigure(encoding='utf-8')

game_id = "ede9eb00-367d-4a87-8b18-64dba8a49710"
base = f"C:\\Users\\clawd\\DartBenchmark\\A3C8DCD1-4196-4BF6-BD20-50310B960745\\{game_id}"
API = "http://127.0.0.1:5000"

# Replay ALL darts with debug info, flag errors
for round_dir in sorted(glob.glob(os.path.join(base, "round_*"))):
    round_name = os.path.basename(round_dir)
    for dart_dir in sorted(glob.glob(os.path.join(round_dir, "dart_*"))):
        dart_name = os.path.basename(dart_dir)
        meta_path = os.path.join(dart_dir, "metadata.json")
        if not os.path.exists(meta_path): continue
        meta = json.load(open(meta_path))
        
        correction = meta.get("correction")
        if correction and correction.get("corrected"):
            truth = correction["corrected"]
            src = "CORR"
        else:
            truth = meta.get("final_result", {})
            src = "det"
        exp_seg = truth.get("segment", 0)
        exp_mult = truth.get("multiplier", 0)
        
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
        if not images: continue

        payload = {"boardId": "default", "images": images, "beforeImages": before_images, "requestId": "debug"}
        r = requests.post(f"{API}/api/games/benchmark/detect", json=payload, timeout=30)
        d = r.json()
        
        darts = d.get("darts", [])
        dart_info = darts[0] if darts else {}
        n_seg = dart_info.get("segment", 0)
        n_mult = dart_info.get("multiplier", 0)
        match = (n_seg == exp_seg and n_mult == exp_mult)
        
        if match: continue  # Only show errors
        
        dl = d.get("debugLines") or {}
        method = d.get("method", "N/A")
        cx = d.get("coordsX")
        cy = d.get("coordsY")
        
        import math
        dist = math.sqrt(cx*cx + cy*cy) if cx is not None and cy is not None else None
        
        seg_match = (n_seg == exp_seg)
        mult_match = (n_mult == exp_mult)
        err_type = "SEGMENT" if not seg_match else "MULTIPLIER"
        
        print(f"\n[{err_type}] {round_name}/{dart_name}: got S{n_seg}x{n_mult}, expected S{exp_seg}x{exp_mult} [{src}]")
        print(f"  Method: {method}  Coords: ({cx}, {cy})  Dist: {dist:.3f}" if dist else f"  Method: {method}  Coords: None")
        
        if dl:
            for cam, info in dl.items():
                lsx = info.get('ls_x', 0); lsy = info.get('ls_y', 0)
                lex = info.get('le_x', 0); ley = info.get('le_y', 0)
                td = info.get('tip_dist', 0); mq = info.get('mask_q', 0)
                print(f"    {cam}: line ({lsx:.3f},{lsy:.3f})->({lex:.3f},{ley:.3f}) tip_d={td:.3f} mq={mq:.0f}")
