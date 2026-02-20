import os, json

game = 'f50ebc9f-b8cf-4331-ac48-129d12e45cac'
base = r'C:\Users\clawd\DartBenchmark\A3C8DCD1-4196-4BF6-BD20-50310B960745' + '\\' + game
dd = os.path.join(base, 'round_3_Player 1', 'dart_2')

print("Directory:", dd)
print("Exists:", os.path.isdir(dd))
print("Files:")
for fn in sorted(os.listdir(dd)):
    fp = os.path.join(dd, fn)
    sz = os.path.getsize(fp)
    print(f"  {fn}  ({sz} bytes)")
