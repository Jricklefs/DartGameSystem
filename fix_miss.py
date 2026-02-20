import sys
path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Controllers\GamesController.cs'
data = open(path, encoding='utf-8').read()

old = '''if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            await _hubContext.SendDartNotFound(boardId);
            return Ok(new { message = "No darts detected", darts = new List<object>() });
        }'''

new = '''if (detectResult == null || detectResult.Tips == null || !detectResult.Tips.Any())
        {
            // Motion detected but no dart tip found — record as a miss (score 0)
            _logger.LogInformation("[{RequestId}] No tip found — recording as MISS", requestId);
            var missDart = new DartThrow
            {
                Index = dartsThisTurn.Count,
                Segment = 0,
                Multiplier = 0,
                Zone = "miss",
                Score = 0,
                XMm = 0,
                YMm = 0,
                Confidence = 0
            };

            if (game.IsX01Engine)
                _x01Engine.ProcessDart(game, missDart);
            else
                _gameService.ApplyManualDart(game, missDart);
            
            await _hubContext.SendDartThrown(game.BoardId, missDart, game);
            return Ok(new { message = "Miss recorded", darts = new[] { new { missDart.Zone, missDart.Score, missDart.Segment, missDart.Multiplier } } });
        }'''

if old in data:
    data = data.replace(old, new)
    open(path, 'w', encoding='utf-8').write(data)
    print("SUCCESS")
else:
    print("ERROR: old text not found")
