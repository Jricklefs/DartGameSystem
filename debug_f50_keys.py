import requests, json, os, base64

game = 'f50ebc9f-b8cf-4331-ac48-129d12e45cac'
base = r'C:\Users\clawd\DartBenchmark\A3C8DCD1-4196-4BF6-BD20-50310B960745' + '\\' + game
dd = os.path.join(base, 'round_3_Player 1', 'dart_2')
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
print(json.dumps(d, indent=2)[:3000])
