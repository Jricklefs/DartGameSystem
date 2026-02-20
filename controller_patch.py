import sys
sys.stdout.reconfigure(encoding='utf-8')

path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\Controllers\GamesController.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Add acknowledge-leg endpoint before the ClearBoard endpoint
old = '''    /// <summary>
    /// Clear the board (darts removed) - triggers rebase
    /// </summary>
    [HttpPost("board/{boardId}/clear")]'''

new = '''    /// <summary>
    /// Acknowledge leg won - player has seen the popup, pause sensor while they pull darts
    /// </summary>
    [HttpPost("{id}/acknowledge-leg")]
    public async Task<ActionResult> AcknowledgeLeg(string id)
    {
        var game = _gameService.GetGame(id);
        if (game == null) return NotFound();
        
        // Pause sensor - player will pull darts
        await _hubContext.SendPauseDetection(game.BoardId);
        _logger.LogInformation("Leg acknowledged on board {BoardId}, sensor paused until board clear", game.BoardId);
        
        return Ok(new { message = "Leg acknowledged, waiting for board clear" });
    }

    /// <summary>
    /// Clear the board (darts removed) - triggers rebase
    /// </summary>
    [HttpPost("board/{boardId}/clear")]'''

content = content.replace(old, new)

# 2. In EventBoardClear, add AwaitingLegClear check before the normal board clear path
old = '''        else if (game != null && !previousTurn.IsBusted)
        {
            // Normal board clear — advance turn (SendTurnEnded sends rebase)
            _gameService.NextTurn(game);
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
            _logger.LogInformation("Board cleared - {DartCount} darts thrown, advancing to next player", dartCount);
        }'''

new = '''        else if (game != null && game.AwaitingLegClear)
        {
            // Board cleared after leg won — new leg already started, just rebase and resume
            game.AwaitingLegClear = false;
            await _hubContext.SendResumeDetection(boardId);
            await _hubContext.SendRebase(boardId);
            var currentPlayer = game.Players.ElementAtOrDefault(game.CurrentPlayerIndex);
            await UpdateBenchmarkContext(game.BoardId, game.Id, game.CurrentRound, currentPlayer?.Name);
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
            _logger.LogInformation("Board cleared after leg won - new leg {Leg} starting, sensor resumed", game.CurrentLeg);
        }
        else if (game != null && !previousTurn.IsBusted)
        {
            // Normal board clear — advance turn (SendTurnEnded sends rebase)
            _gameService.NextTurn(game);
            await _hubContext.SendTurnEnded(game.BoardId, game, previousTurn);
            _logger.LogInformation("Board cleared - {DartCount} darts thrown, advancing to next player", dartCount);
        }'''

content = content.replace(old, new)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("GamesController.cs patched")
