import requests, json, os, base64, math, sys

game = 'f50ebc9f-b8cf-4331-ac48-129d12e45cac'
base = r'C:\Users\clawd\DartBenchmark\A3C8DCD1-4196-4BF6-BD20-50310B960745' + '\\' + game
errs = [
    ('round_3_Player 1', 'dart_2', 'S20->T20'),
    ('round_3_Player 1', 'dart_3', 'T20->S5'),
    ('round_8_Player 1', 'dart_1', 'S12->S9'),
    ('round_8_Player 1', 'dart_2', 'S0->outer_bull'),
]

def load_b64(path):
    with open(path, 'rb') as f:
        return base64.b64encode(f.read()).decode()

for rd, dt, desc in errs:
    dd = os.path.join(base, rd, dt)
    images = []
    before_images = []
    for cam in ['cam0', 'cam1', 'cam2']:
        raw = os.path.join(dd, f'{cam}_raw.jpg')
        if not os.path.exists(raw):
            raw = os.path.join(dd, f'{cam}_raw.png')
        prev = os.path.join(dd, f'{cam}_previous.jpg')
        if not os.path.exists(prev):
            prev = os.path.join(dd, f'{cam}_previous.png')
        if os.path.exists(raw):
            images.append({'cameraId': cam, 'image': load_b64(raw)})
        if os.path.exists(prev):
            before_images.append({'cameraId': cam, 'image': load_b64(prev)})

    payload = {
        'boardId': 'default',
        'images': images,
        'beforeImages': before_images,
        'requestId': f'debug_{rd}_{dt}'
    }

    r = requests.post('http://localhost:5000/api/games/benchmark/detect', json=payload, timeout=30)
    d = r.json()

    darts = d.get('darts', [])
    if not darts:
        print(f'=== {rd}/{dt} ({desc}) === NO DARTS DETECTED')
        print()
        continue

    dart = darts[0]
    cx = dart.get('coordsX', 0) or 0
    cy = dart.get('coordsY', 0) or 0
    dist = math.sqrt(cx * cx + cy * cy)
    ang = math.degrees(math.atan2(cy, -cx))

    print(f'=== {rd}/{dt} ({desc}) ===')
    print(f'  zone={dart.get("zone")} seg={dart.get("segment")} mult={dart.get("multiplier")} method={dart.get("method")}')
    print(f'  coords=({cx:.4f},{cy:.4f}) dist={dist:.4f} angle={ang:.1f}')

    dl = dart.get('debugLines') or {}
    for c, i in sorted(dl.items()):
        print(f'  {c}: tip_n=({i.get("tip_nx",0):.4f},{i.get("tip_ny",0):.4f}) dist={i.get("tip_dist",0):.4f} mq={i.get("mask_q",0):.3f} rel={i.get("tip_reliable")}')

    # Ring boundary context
    if dist > 0:
        print(f'  --- Ring context: triple=[0.582-0.629] double=[0.953-1.000] bull=[0.037-0.094]')
        if 0.5 < dist < 0.7:
            print(f'      Distance from triple_inner: {abs(dist-0.582):.4f}, from triple_outer: {abs(dist-0.629):.4f}')
        elif 0.9 < dist < 1.1:
            print(f'      Distance from double_inner: {abs(dist-0.953):.4f}, from double_outer: {abs(dist-1.000):.4f}')
        elif dist < 0.15:
            print(f'      Distance from bullseye: {abs(dist-0.037):.4f}, from bull: {abs(dist-0.094):.4f}')

    # Segment boundary context
    seg = dart.get('segment', 0)
    adj_angle = (ang - 90 + 9 + 360) % 360
    seg_center = (adj_angle % 18) - 9
    print(f'      Segment angle offset from center: {seg_center:.1f} deg (boundary at +/-9)')

    print()
    sys.stdout.flush()
