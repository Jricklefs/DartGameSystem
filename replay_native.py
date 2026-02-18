"""Benchmark replay through DartsMob API (native C++ detection)."""
import os, sys, json, base64, glob, requests, time

DARTSMOB_URL = "http://127.0.0.1:5000"
BENCHMARK_ROOT = r"C:\Users\clawd\DartBenchmark\A3C8DCD1-4196-4BF6-BD20-50310B960745"

def load_image_b64(path):
    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode()

def replay_game(game_id):
    game_dir = os.path.join(BENCHMARK_ROOT, game_id)
    rounds = sorted(glob.glob(os.path.join(game_dir, "round_*")))
    
    total = 0; correct = 0; times = []
    
    for round_dir in rounds:
        round_name = os.path.basename(round_dir)
        for dart_dir in sorted(glob.glob(os.path.join(round_dir, "dart_*"))):
            dart_name = os.path.basename(dart_dir)
            meta_path = os.path.join(dart_dir, "metadata.json")
            if not os.path.exists(meta_path): continue
            
            meta = json.load(open(meta_path))
            correction = meta.get("correction")
            if correction and correction.get("corrected"):
                # Use the human-corrected score as ground truth
                truth = correction["corrected"]
                truth_source = "CORRECTED"
            else:
                truth = meta.get("final_result", {})
                truth_source = "detected"
            exp_seg = truth.get("segment", 0)
            exp_mult = truth.get("multiplier", 0)
            
            images = []
            before_images = []
            for cam_id in ["cam0", "cam1", "cam2"]:
                raw = os.path.join(dart_dir, f"{cam_id}_raw.png")
                prev = os.path.join(dart_dir, f"{cam_id}_previous.png")
                if os.path.exists(raw):
                    images.append({"cameraId": cam_id, "image": load_image_b64(raw)})
                if os.path.exists(prev):
                    before_images.append({"cameraId": cam_id, "image": load_image_b64(prev)})
            
            if not images: continue
            
            dart_num = int(dart_name.split("_")[1])
            payload = {
                "boardId": "default",
                "images": images,
                "beforeImages": before_images,
                "requestId": f"bench_{round_name}_{dart_name}"
            }
            
            start = time.time()
            resp = requests.post(f"{DARTSMOB_URL}/api/games/benchmark/detect", json=payload, timeout=30)
            elapsed_ms = int((time.time() - start) * 1000)
            result = resp.json()
            times.append(elapsed_ms)
            
            darts = result.get("darts", [])
            if darts:
                n_seg = darts[0].get("segment", 0)
                n_mult = darts[0].get("multiplier", 0)
                n_zone = darts[0].get("zone", "?")
            else:
                n_seg = n_mult = 0; n_zone = "MISS"
            
            match = (n_seg == exp_seg and n_mult == exp_mult)
            total += 1
            if match: correct += 1
            
            exp_zone = truth.get("zone", "?")
            mark = "OK" if match else "XX"
            src = f" [{truth_source}]" if truth_source == "CORRECTED" else ""
            print(f"  [{mark}] {round_name}/{dart_name}: native={n_zone} S{n_seg}x{n_mult}  |  expected={exp_zone} S{exp_seg}x{exp_mult}{src}  [{elapsed_ms}ms]")
    
    print(f"\n{'='*60}")
    pct = 100*correct/total if total else 0
    print(f"MATCH vs PYTHON: {correct}/{total} ({pct:.0f}%)")
    if times:
        print(f"AVG TIME: {sum(times)//len(times)}ms  |  MIN: {min(times)}ms  |  MAX: {max(times)}ms")
    print(f"{'='*60}")

if __name__ == "__main__":
    game_id = sys.argv[1] if len(sys.argv) > 1 else "203ab090-68ec-4401-9a66-01249a39833b"
    print(f"Replaying game {game_id} through DartsMob API (native C++ detection)")
    print(f"{'='*60}")
    replay_game(game_id)
