import sys
sys.stdout.reconfigure(encoding='utf-8')

path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Models\GameModels.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Add AwaitingLegClear property after LegWinnerId
old = '    public string? LegWinnerId { get; set; }  // Who won the current/last leg'
new = '''    public string? LegWinnerId { get; set; }  // Who won the current/last leg
    public bool AwaitingLegClear { get; set; } = false;  // True after leg won, waiting for board clear'''

content = content.replace(old, new)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("GameModels.cs patched")
