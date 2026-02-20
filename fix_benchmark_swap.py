import re

path = r"C:\Users\clawd\DartGameSystem\DartGameAPI\Controllers\GamesController.cs"
with open(path, 'r') as f:
    content = f.read()

# Swap request.Images and request.BeforeImages in the benchmark save call
old = "request.Images, request.BeforeImages, newTip, detectResult));"
new = "request.BeforeImages, request.Images, newTip, detectResult));  // BeforeImages=raw(with dart), Images=previous(before dart)"

if old in content:
    content = content.replace(old, new, 1)  # Only replace the first occurrence (line ~165)
    with open(path, 'w') as f:
        f.write(content)
    print(f"Fixed: swapped Images/BeforeImages in benchmark save")
else:
    print("Pattern not found!")
