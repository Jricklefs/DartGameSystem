import requests, json, os, base64, math, sys

game = 'f50ebc9f-b8cf-4331-ac48-129d12e45cac'
base = r'C:\Users\clawd\DartBenchmark\A3C8DCD1-4196-4BF6-BD20-50310B960745' + '\\' + game
errs = [
    ('round_3_Player 1', 'dart_2'),
    ('round_3_Player 1', 'dart_3'),
    ('round_8_Player 1', 'dart_1'),
    ('round_8_Player 1', 'dart_2'),
]

for rd, dt in errs:
    dd = os.path.join(base, rd, dt)
    payload = {'game_id': game}
    for fn in os.listdir(dd):
        fp = os.path.join(dd, fn)
        if fn.endswith('.json'):
            continue
        if '_raw' in fn:
            cam = fn.split('_')[0]
            with open(fp, 'rb') as f:
                payload['after_' + cam] = base64.b64encode(f.read()).decode()
        elif '_previous' in fn:
            cam = fn.split('_')[0]
            with open(fp, 'rb') as f:
                payload['before_' + cam] = base64.b64encode(f.read()).decode()

    r = requests.post('http://localhost:5000/api/games/benchmark/detect', json=payload, timeout=30)
    d = r.json()
    cx = d.get('coordsX', 0) or 0
    cy = d.get('coordsY', 0) or 0
    dist = math.sqrt(cx * cx + cy * cy)
    ang = math.degrees(math.atan2(cy, -cx))
    print(f'=== {rd}/{dt} ===')
    print(f'  zone={d.get("zone")} seg={d.get("segment")} mult={d.get("multiplier")} method={d.get("method")}')
    print(f'  coords=({cx:.4f},{cy:.4f}) dist={dist:.4f} angle={ang:.1f}')
    dl = d.get('debugLines') or {}
    for c, i in sorted(dl.items()):
        print(f'  {c}: tip_n=({i.get("tip_nx",0):.4f},{i.get("tip_ny",0):.4f}) dist={i.get("tip_dist",0):.4f} mq={i.get("mask_q",0):.3f} rel={i.get("tip_reliable")}')
    print()
    sys.stdout.flush()
