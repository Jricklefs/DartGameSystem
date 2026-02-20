import sys
path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Controllers\GamesController.cs'
data = open(path, encoding='utf-8').read()

old = """        _gameService.CorrectDart(game, request.DartIndex, newDart);

        if (_benchmark.IsEnabled)
        {
            var corrPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex)?.Name ?? "player";
            _ = Task.Run(() => _benchmark.SaveCorrectionAsync(
                game.BoardId, game.Id, game.CurrentRound, corrPlayer, request.DartIndex + 1, oldDart, newDart));
        }

        _ = RecordBenchmarkCorrection(game, request.DartIndex, oldDart, newDart);

        await _hubContext.SendDartThrown(game.BoardId, newDart, game);

        if (game.State == GameState.Finished)
            await _hubContext.SendGameEnded(game.BoardId, game);
        else if (game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }

        return Ok(new ThrowResult { NewDart = newDart, Game = game });"""

new = """        // Route through X01 engine for X01 games, fallback to GameService for others
        DartResult correctionResult = null;
        if (game.IsX01Engine)
        {
            var currentPlayer = game.CurrentPlayer;
            if (currentPlayer != null)
                correctionResult = _x01Engine.CorrectDart(game, currentPlayer.Id, request.DartIndex, newDart);
        }
        else
        {
            _gameService.CorrectDart(game, request.DartIndex, newDart);
        }

        if (_benchmark.IsEnabled)
        {
            var corrPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex)?.Name ?? "player";
            _ = Task.Run(() => _benchmark.SaveCorrectionAsync(
                game.BoardId, game.Id, game.CurrentRound, corrPlayer, request.DartIndex + 1, oldDart, newDart));
        }

        _ = RecordBenchmarkCorrection(game, request.DartIndex, oldDart, newDart);

        await _hubContext.SendDartThrown(game.BoardId, newDart, game);

        // Check if correction resulted in checkout or match end
        if (game.State == GameState.Finished)
            await _hubContext.SendGameEnded(game.BoardId, game);
        else if (correctionResult?.Type == DartResultType.LegWon || game.LegWinnerId != null)
        {
            var legWinner = game.Players.FirstOrDefault(p => p.Id == game.LegWinnerId);
            if (legWinner != null)
                await _hubContext.SendLegWon(game.BoardId, legWinner.Name, legWinner.LegsWon, game.LegsToWin, game);
        }
        else if (correctionResult?.Type == DartResultType.Bust)
        {
            // Correction caused a bust — send updated game state
            await _hubContext.SendDartThrown(game.BoardId, newDart, game);
        }

        return Ok(new ThrowResult { NewDart = newDart, Game = game });"""

if old in data:
    data = data.replace(old, new)
    open(path, 'w', encoding='utf-8').write(data)
    print("SUCCESS")
else:
    print("ERROR: old text not found")
    # Debug
    idx = data.find("_gameService.CorrectDart")
    print(f"Found _gameService.CorrectDart at index: {idx}")
    if idx > 0:
        print(repr(data[idx:idx+200]))
