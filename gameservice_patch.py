import sys
sys.stdout.reconfigure(encoding='utf-8')

path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Services\GameService.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# In StartNextLeg, add AwaitingLegClear = true after game.CurrentLeg++
old = '''    private void StartNextLeg(Game game)
    {
        game.CurrentLeg++;
        // NOTE: Don't clear LegWinnerId here - controller needs it for SendLegWon event
        // It gets cleared at the start of the next ApplyManualDart call'''
new = '''    private void StartNextLeg(Game game)
    {
        game.CurrentLeg++;
        game.AwaitingLegClear = true;  // Wait for board clear before resuming
        // NOTE: Don't clear LegWinnerId here - controller needs it for SendLegWon event
        // It gets cleared at the start of the next ApplyManualDart call'''

content = content.replace(old, new)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("GameService.cs patched")
