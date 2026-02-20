"""Plot warped lines from C++ debug output on a dartboard."""
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.patches as patches
import numpy as np
import json, sys, os, base64, requests

SEGMENT_ORDER = [20,1,18,4,13,6,10,15,2,17,3,19,7,16,8,11,14,9,12,5]
NORM_RINGS = {
    'bullseye': 6.35/170,
    'bull': 16.0/170,
    'triple_inner': 99.0/170,
    'triple_outer': 107.0/170,
    'double_inner': 162.0/170,
    'double_outer': 1.0,
}
COLORS = {'cam0': '#FF4444', 'cam1': '#44FF44', 'cam2': '#4488FF'}

def draw_board(ax):
    """Draw dartboard rings and segment boundaries."""
    for name, r in NORM_RINGS.items():
        circle = plt.Circle((0, 0), r, fill=False, color='#555555', linewidth=0.8)
        ax.add_patch(circle)
    
    # Segment boundaries and labels
    for i in range(20):
        # Boundary angle: center of segment i is at i*18 deg, boundary at i*18-9 deg
        angle_deg = i * 18.0 - 9.0
        angle_rad = np.radians(angle_deg)
        x = np.sin(angle_rad)
        y = np.cos(angle_rad)
        # Line from bull to board edge
        ax.plot([NORM_RINGS['bull']*x, x], [NORM_RINGS['bull']*y, y], 
                color='#555555', linewidth=0.5)
        
        # Segment label
        label_angle_deg = i * 18.0
        label_angle_rad = np.radians(label_angle_deg)
        lx = 1.12 * np.sin(label_angle_rad)
        ly = 1.12 * np.cos(label_angle_rad)
        ax.text(lx, ly, str(SEGMENT_ORDER[i]), ha='center', va='center', 
                fontsize=7, color='white', fontweight='bold')

def plot_from_debug(debug_lines, coords, title="Warped Lines", output="warped_lines.png"):
    fig, ax = plt.subplots(1, 1, figsize=(8, 8), facecolor='#1a1a1a')
    ax.set_facecolor('#1a1a1a')
    ax.set_xlim(-1.35, 1.35)
    ax.set_ylim(-1.35, 1.35)
    ax.set_aspect('equal')
    ax.set_title(title, color='white', fontsize=14)
    
    draw_board(ax)
    
    for cam_id, info in debug_lines.items():
        color = COLORS.get(cam_id, '#FFFFFF')
        ls_x, ls_y = info['ls_x'], info['ls_y']
        le_x, le_y = info['le_x'], info['le_y']
        tip_nx, tip_ny = info['tip_nx'], info['tip_ny']
        
        # Draw warped line (extend as ray for visibility)
        dx, dy = le_x - ls_x, le_y - ls_y
        length = np.sqrt(dx*dx + dy*dy)
        if length > 0:
            dx, dy = dx/length, dy/length
        # Extend line to cover full board
        ext = 2.0
        x1 = le_x - dx * ext
        y1 = le_y - dy * ext
        x2 = le_x + dx * ext
        y2 = le_y + dy * ext
        ax.plot([x1, x2], [y1, y2], color=color, linewidth=1.5, alpha=0.6, linestyle='--')
        
        # Draw the actual line segment
        ax.plot([ls_x, le_x], [ls_y, le_y], color=color, linewidth=2.5, alpha=0.9)
        
        # Mark line start (flight end) with small circle
        ax.plot(ls_x, ls_y, 'o', color=color, markersize=4, alpha=0.6)
        
        # Mark line end (tip end) with diamond
        ax.plot(le_x, le_y, 'D', color=color, markersize=6, alpha=0.9)
        
        # Mark tip normalized position
        ax.plot(tip_nx, tip_ny, 'x', color=color, markersize=8, markeredgewidth=2)
        
        # Label
        ax.annotate(cam_id, (le_x, le_y), textcoords="offset points", 
                    xytext=(8, 8), color=color, fontsize=9, fontweight='bold')
    
    # Mark intersection point
    if coords:
        ax.plot(coords[0], coords[1], '*', color='#FFD700', markersize=18, 
                markeredgecolor='white', markeredgewidth=0.5, zorder=10)
        # Score the point
        angle_rad = np.arctan2(coords[1], -coords[0])
        angle_deg = np.degrees(angle_rad)
        if angle_deg < 0: angle_deg += 360
        adjusted = (angle_deg - 90 + 9 + 360) % 360
        seg_idx = int(adjusted / 18) % 20
        dist = np.sqrt(coords[0]**2 + coords[1]**2)
        seg = SEGMENT_ORDER[seg_idx]
        ax.annotate(f'S{seg} ({dist:.3f})', (coords[0], coords[1]), 
                    textcoords="offset points", xytext=(12, -12), 
                    color='#FFD700', fontsize=11, fontweight='bold')
    
    # Legend
    legend_items = []
    for cam_id in sorted(debug_lines.keys()):
        color = COLORS.get(cam_id, '#FFFFFF')
        reliable = debug_lines[cam_id].get('tip_reliable', False)
        mq = debug_lines[cam_id].get('mask_q', 0)
        td = debug_lines[cam_id].get('tip_dist', 0)
        label = f"{cam_id} (mq={mq:.0f}, td={td:.3f}, rel={'Y' if reliable else 'N'})"
        legend_items.append(plt.Line2D([0], [0], color=color, linewidth=2, label=label))
    ax.legend(handles=legend_items, loc='upper left', fontsize=8, 
              facecolor='#333333', edgecolor='#666666', labelcolor='white')
    
    plt.tight_layout()
    plt.savefig(output, dpi=150, facecolor='#1a1a1a')
    plt.close()
    print(f"Saved: {output}")

def replay_dart_with_debug(game_id, round_name, dart_name, benchmark_root, api_url="http://127.0.0.1:5000"):
    """Replay a specific dart and get debug output."""
    game_dir = os.path.join(benchmark_root, game_id)
    dart_dir = os.path.join(game_dir, round_name, dart_name)
    
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
    r = requests.post(f"{api_url}/api/games/benchmark/detect", json=payload, timeout=30)
    return r.json()

if __name__ == "__main__":
    # Usage: python plot_warped_lines.py <game_id> <round_name> <dart_name>
    # Or: python plot_warped_lines.py --all <game_id>  (plots all darts)
    
    BENCHMARK_ROOT = r"C:\Users\clawd\DartBenchmark\A3C8DCD1-4196-4BF6-BD20-50310B960745"
    API_URL = "http://127.0.0.1:5000"
    
    if len(sys.argv) >= 4:
        game_id = sys.argv[1]
        round_name = sys.argv[2]
        dart_name = sys.argv[3]
        
        print(f"Replaying {round_name}/{dart_name}...")
        d = replay_dart_with_debug(game_id, round_name, dart_name, BENCHMARK_ROOT, API_URL)
        
        debug_lines = d.get("debugLines", {})
        coords = None
        if d.get("coordsX") is not None:
            coords = (d["coordsX"], d["coordsY"])
        
        dart_info = d.get("darts", [{}])[0] if d.get("darts") else {}
        seg = dart_info.get("segment", "?")
        mult = dart_info.get("multiplier", "?")
        
        output = f"warped_{round_name}_{dart_name}.png".replace(" ", "_")
        plot_from_debug(debug_lines, coords, 
                       f"{round_name}/{dart_name}: S{seg}x{mult}", output)
    
    elif len(sys.argv) >= 3 and sys.argv[1] == "--all":
        game_id = sys.argv[2]
        game_dir = os.path.join(BENCHMARK_ROOT, game_id)
        import glob
        
        os.makedirs("warped_plots", exist_ok=True)
        for round_dir in sorted(glob.glob(os.path.join(game_dir, "round_*"))):
            round_name = os.path.basename(round_dir)
            for dart_d in sorted(glob.glob(os.path.join(round_dir, "dart_*"))):
                dart_name = os.path.basename(dart_d)
                print(f"Replaying {round_name}/{dart_name}...")
                try:
                    d = replay_dart_with_debug(game_id, round_name, dart_name, BENCHMARK_ROOT, API_URL)
                    debug_lines = d.get("debugLines", {})
                    coords = None
                    if d.get("coordsX") is not None:
                        coords = (d["coordsX"], d["coordsY"])
                    dart_info = d.get("darts", [{}])[0] if d.get("darts") else {}
                    seg = dart_info.get("segment", "?")
                    mult = dart_info.get("multiplier", "?")
                    output = os.path.join("warped_plots", 
                        f"warped_{round_name}_{dart_name}.png".replace(" ", "_"))
                    plot_from_debug(debug_lines, coords, 
                                   f"{round_name}/{dart_name}: S{seg}x{mult}", output)
                except Exception as e:
                    print(f"  ERROR: {e}")
    else:
        print("Usage:")
        print("  python plot_warped_lines.py <game_id> <round_name> <dart_name>")
        print("  python plot_warped_lines.py --all <game_id>")
        print()
        print("Example:")
        print("  python plot_warped_lines.py e202e039-... \"round_2_Player 1\" dart_1")
