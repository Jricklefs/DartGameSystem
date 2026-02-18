"""Visualize C++ calibration overlay — segment labels centered in wedges."""
import json, math, os
import numpy as np
import cv2
import pyodbc

CONN_STR = "Driver={ODBC Driver 17 for SQL Server};Server=JOESSERVER2019;Database=DartsMobDB;Uid=DartsMobApp;Pwd=Stewart14s!2;TrustServerCertificate=Yes;"
SEGMENT_ORDER = [20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5]

def load_calibrations():
    conn = pyodbc.connect(CONN_STR)
    cur = conn.cursor()
    cur.execute("""
        SELECT c.CameraId, c.CalibrationData FROM (
            SELECT CameraId, CalibrationData,
                   ROW_NUMBER() OVER (PARTITION BY CameraId ORDER BY CreatedAt DESC) as rn
            FROM Calibrations
        ) c WHERE c.rn = 1
    """)
    cals = {}
    for row in cur.fetchall():
        cals[row[0]] = json.loads(row[1])
    conn.close()
    return cals

def ellipse_point_at_angle(ell, angle_rad, cx, cy):
    e_cx, e_cy = ell[0]
    w, h = ell[1]
    rot_deg = ell[2]
    a, b = w/2.0, h/2.0
    rot = math.radians(rot_deg)
    cos_r, sin_r = math.cos(rot), math.sin(rot)
    dx, dy = math.cos(angle_rad), math.sin(angle_rad)
    ox, oy = cx - e_cx, cy - e_cy
    u0 = ox*cos_r + oy*sin_r
    du = dx*cos_r + dy*sin_r
    v0 = -ox*sin_r + oy*cos_r
    dv = -dx*sin_r + dy*cos_r
    A = du**2/(a**2) + dv**2/(b**2)
    B = 2*(u0*du/(a**2) + v0*dv/(b**2))
    C = u0**2/(a**2) + v0**2/(b**2) - 1
    disc = B**2 - 4*A*C
    if disc < 0: return None
    t1 = (-B + math.sqrt(disc))/(2*A)
    t2 = (-B - math.sqrt(disc))/(2*A)
    t = min(t1,t2) if min(t1,t2) > 0 else max(t1,t2)
    if t <= 0: return None
    return (cx + t*dx, cy + t*dy)

def draw_overlay(img, cal, cam_id):
    bcx, bcy = cal['center'][0], cal['center'][1]
    seg_angles = cal['segment_angles']
    seg20_idx = cal.get('segment_20_index', 0)
    
    result = img.copy()
    
    # Draw rings
    ring_draw = [
        ('outer_double_ellipse', (255, 0, 0), 2, "Double"),
        ('inner_double_ellipse', (255, 100, 0), 1, ""),
        ('outer_triple_ellipse', (0, 255, 0), 2, "Triple"),
        ('inner_triple_ellipse', (0, 200, 0), 1, ""),
        ('bull_ellipse', (0, 165, 255), 2, "Bull"),
        ('bullseye_ellipse', (0, 0, 255), 2, "Bullseye"),
    ]
    
    for ring_key, color, thickness, label in ring_draw:
        ell = cal.get(ring_key)
        if not ell: continue
        center_pt = (int(ell[0][0]), int(ell[0][1]))
        axes = (int(ell[1][0]/2), int(ell[1][1]/2))
        angle = ell[2]
        cv2.ellipse(result, center_pt, axes, angle, 0, 360, color, thickness)
    
    # Draw segment boundary lines with labels like "20|1"
    for idx in range(20):
        angle = seg_angles[idx]
        board_idx = (idx - seg20_idx) % 20
        # This boundary is between segment board_idx-1 and board_idx
        seg_left = SEGMENT_ORDER[(board_idx - 1) % 20]
        seg_right = SEGMENT_ORDER[board_idx]
        
        bull_ell = cal.get('bull_ellipse')
        outer_ell = cal.get('outer_double_ellipse')
        inner_pt = ellipse_point_at_angle(bull_ell, angle, bcx, bcy) if bull_ell else None
        outer_pt = ellipse_point_at_angle(outer_ell, angle, bcx, bcy) if outer_ell else None
        
        if inner_pt and outer_pt:
            cv2.line(result, (int(inner_pt[0]), int(inner_pt[1])),
                     (int(outer_pt[0]), int(outer_pt[1])), (0, 255, 255), 1)
            # Label on boundary line: "left|right"
            mid_x = int((inner_pt[0] + outer_pt[0]) / 2)
            mid_y = int((inner_pt[1] + outer_pt[1]) / 2)
            label = f"{seg_left}|{seg_right}"
            cv2.putText(result, label, (mid_x - 15, mid_y + 4),
                       cv2.FONT_HERSHEY_SIMPLEX, 0.3, (0, 200, 200), 1)
    
    # Draw segment NUMBERS centered in each wedge
    for idx in range(20):
        board_idx = (idx - seg20_idx) % 20
        seg_num = SEGMENT_ORDER[board_idx]
        
        # Midpoint angle between this boundary and next
        a1 = seg_angles[idx]
        a2 = seg_angles[(idx + 1) % 20]
        # Average angle (handle wraparound)
        if abs(a2 - a1) > math.pi:
            if a2 < a1: a2 += 2*math.pi
            else: a1 += 2*math.pi
        mid_angle = (a1 + a2) / 2.0
        
        # Place label at ~75% radius (single outer zone)
        outer_ell = cal.get('outer_double_ellipse')
        triple_outer = cal.get('outer_triple_ellipse')
        if outer_ell and triple_outer:
            outer_pt = ellipse_point_at_angle(outer_ell, mid_angle, bcx, bcy)
            triple_pt = ellipse_point_at_angle(triple_outer, mid_angle, bcx, bcy)
            if outer_pt and triple_pt:
                # Midpoint of single outer zone
                lx = int((outer_pt[0] + triple_pt[0]) / 2)
                ly = int((outer_pt[1] + triple_pt[1]) / 2)
                # Draw background rectangle for readability
                tw, th = cv2.getTextSize(str(seg_num), cv2.FONT_HERSHEY_SIMPLEX, 0.6, 2)[0]
                cv2.rectangle(result, (lx-tw//2-2, ly-th-2), (lx+tw//2+2, ly+4), (0,0,0), -1)
                cv2.putText(result, str(seg_num), (lx - tw//2, ly),
                           cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
    
    # Center dot
    cv2.circle(result, (int(bcx), int(bcy)), 4, (0, 0, 255), -1)
    
    # Title
    cv2.putText(result, f"{cam_id} - C++ Calibration Overlay (seg20_idx={seg20_idx})", (10, 25),
               cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
    
    return result

def main():
    print("Loading calibrations from DB...")
    cals = load_calibrations()
    print(f"Loaded: {list(cals.keys())}")
    
    out_dir = r"C:\Users\clawd\DartGameSystem\debug_overlays"
    os.makedirs(out_dir, exist_ok=True)
    
    # Grab live frames
    import requests, base64
    for cam_id in ["cam0", "cam1", "cam2"]:
        if cam_id not in cals:
            print(f"  {cam_id}: no calibration")
            continue
        
        idx = int(cam_id[-1])
        try:
            r = requests.get(f"http://127.0.0.1:8001/cameras/{idx}/snapshot", timeout=5)
            img_bytes = base64.b64decode(r.json()["image"])
            img_arr = np.frombuffer(img_bytes, np.uint8)
            img = cv2.imdecode(img_arr, cv2.IMREAD_COLOR)
        except Exception as e:
            print(f"  {cam_id}: failed to get snapshot: {e}")
            continue
        
        overlay = draw_overlay(img, cals[cam_id], cam_id)
        out_path = os.path.join(out_dir, f"{cam_id}_cpp_overlay.png")
        cv2.imwrite(out_path, overlay)
        print(f"  {cam_id}: saved {out_path}")

if __name__ == "__main__":
    main()
