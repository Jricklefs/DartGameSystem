path = r"C:\Users\clawd\DartGameSystem\DartGameAPI\Controllers\GamesController.cs"
with open(path, 'r') as f:
    content = f.read()

# Find the miss return statement and add benchmark save before it
old = '''            await _hubContext.SendDartThrown(game.BoardId, missDart, game);
            return Ok(new { message = "Miss recorded", darts = new[] { new { missDart.Zone, missDart.Score, missDart.Segment, missDart.Multiplier } } });'''

new = '''            await _hubContext.SendDartThrown(game.BoardId, missDart, game);

            // Save benchmark data for misses too
            if (_benchmark.IsEnabled)
            {
                var bmPlayer = player?.Name ?? "player";
                _ = Task.Run(() => _benchmark.SaveBenchmarkDataAsync(
                    requestId, dartNumber, boardId, game.Id, game.CurrentRound, bmPlayer,
                    request.BeforeImages, request.Images, null, detectResult));
            }

            return Ok(new { message = "Miss recorded", darts = new[] { new { missDart.Zone, missDart.Score, missDart.Segment, missDart.Multiplier } } });'''

if old in content:
    content = content.replace(old, new, 1)
    with open(path, 'w') as f:
        f.write(content)
    print("Fixed: added benchmark save for misses")
else:
    print("Pattern not found!")
