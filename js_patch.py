import sys
sys.stdout.reconfigure(encoding='utf-8')

path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\wwwroot\js\dartsmob.js'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix the LegWon Continue button to call acknowledge-leg endpoint
old = """        document.getElementById('leg-won-ok').addEventListener('click', () => {
            modal.classList.remove('show');
        });"""

new = """        document.getElementById('leg-won-ok').addEventListener('click', async () => {
            modal.classList.remove('show');
            // Acknowledge leg won - pauses sensor while player pulls darts
            if (currentGame?.id) {
                try {
                    await fetch(`/api/games/${currentGame.id}/acknowledge-leg`, { method: 'POST' });
                    console.log('Leg acknowledged, sensor paused for dart removal');
                } catch (e) {
                    console.error('Failed to acknowledge leg:', e);
                }
            }
            // Update scoreboard for new leg
            if (currentGame) {
                updateScoreboard();
                clearCurrentTurn();
            }
        });"""

content = content.replace(old, new)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("dartsmob.js patched")
