using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Manages game state in memory
/// </summary>
public class GameService
{
    private readonly Dictionary<string, Board> _boards = new();
    private readonly Dictionary<string, Game> _games = new();
    private readonly ILogger<GameService> _logger;
    
    // Distance threshold to consider two tips as same dart (mm)
    private const double POSITION_TOLERANCE_MM = 20.0;

    public GameService(ILogger<GameService> logger)
    {
        _logger = logger;
    }

    #region Boards

    public void RegisterBoard(string id, string name, List<string> cameraIds)
    {
        _boards[id] = new Board
        {
            Id = id,
            Name = name,
            CameraIds = cameraIds
        };
    }

    public Board? GetBoard(string id) => _boards.GetValueOrDefault(id);
    
    public IEnumerable<Board> GetAllBoards() => _boards.Values;

    #endregion

    #region Games

    public Game CreateGame(string boardId, GameMode mode, List<string> playerNames, int bestOf = 5, bool requireDoubleOut = false)
    {
        var board = GetBoard(boardId);
        if (board == null)
            throw new InvalidOperationException($"Board '{boardId}' not found");

        // Calculate legs to win (best of 5 = first to 3)
        int legsToWin = (bestOf / 2) + 1;

        var game = new Game
        {
            BoardId = boardId,
            Mode = mode,
            State = GameState.InProgress,
            LegsToWin = legsToWin,
            RequireDoubleOut = requireDoubleOut,
            CurrentLeg = 1,
            Players = playerNames.Select(name => new Player
            {
                Name = name,
                Score = mode switch
                {
                    GameMode.Game501 => 501,
                    GameMode.Game301 => 301,
                    _ => 0
                },
                LegsWon = 0
            }).ToList()
        };

        // Start first turn
        if (game.Players.Any())
        {
            game.CurrentTurn = new Turn
            {
                TurnNumber = 1,
                PlayerId = game.Players[0].Id
            };
        }

        _games[game.Id] = game;
        board.CurrentGameId = game.Id;
        
        _logger.LogInformation("Created game {GameId} on board {BoardId}, mode {Mode}", 
            game.Id, boardId, mode);

        return game;
    }

    public Game? GetGame(string id) => _games.GetValueOrDefault(id);

    public Game? GetGameForBoard(string boardId)
    {
        var board = GetBoard(boardId);
        if (board?.CurrentGameId == null) return null;
        return GetGame(board.CurrentGameId);
    }

    public void EndGame(string id)
    {
        if (_games.TryGetValue(id, out var game))
        {
            game.State = GameState.Finished;
            game.EndedAt = DateTime.UtcNow;
            
            var board = GetBoard(game.BoardId);
            if (board != null) board.CurrentGameId = null;
        }
    }

    #endregion

    #region Dart Detection & Game Logic

    /// <summary>
    /// Apply dart score to game state.
    /// Called when DartDetect pushes a detection via POST /dart-detected.
    /// </summary>
    private void ApplyDartToGame(Game game, DartThrow dart)
    {
        var player = game.CurrentPlayer;
        if (player == null) return;

        // Add to current turn
        game.CurrentTurn ??= new Turn
        {
            TurnNumber = player.Turns.Count + 1,
            PlayerId = player.Id
        };
        player.DartsThrown++;

        // Apply score based on game mode
        switch (game.Mode)
        {
            case GameMode.Practice:
                game.CurrentTurn.Darts.Add(dart);
                player.Score += dart.Score;
                break;

            case GameMode.Game501:
            case GameMode.Game301:
                var newScore = player.Score - dart.Score;
                
                _logger.LogInformation("X01 scoring: player={Name}, current={Current}, dart={Dart}, newScore={New}, multiplier={Mult}",
                    player.Name, player.Score, dart.Score, newScore, dart.Multiplier);
                
                // Bust check: negative, exactly 1 (can't checkout), or 0 without double (if RequireDoubleOut)
                bool isBust = newScore < 0 || 
                              (newScore == 1 && game.RequireDoubleOut) ||  // Can't checkout from 1 with double-out
                              (newScore == 0 && game.RequireDoubleOut && dart.Multiplier != 2);
                if (isBust)
                {
                    _logger.LogInformation("BUST detected: newScore={New}, multiplier={Mult}", newScore, dart.Multiplier);
                    
                    // Store the score at the START of this turn (before any darts this turn)
                    // This is what we revert to on bust
                    var scoreAtTurnStart = player.Score + game.CurrentTurn.Darts.Sum(d => d.Score);
                    game.CurrentTurn.ScoreBeforeBust = scoreAtTurnStart;
                    
                    // Revert player score to start of turn
                    player.Score = scoreAtTurnStart;
                    
                    // Add the bust dart to the record
                    game.CurrentTurn.Darts.Add(dart);
                    
                    // Mark as busted - UI will show "BUSTED" screen and allow corrections
                    // Turn will end when UI calls ConfirmBust or after correction
                    game.CurrentTurn.IsBusted = true;
                    
                    // DON'T call EndTurn here - wait for UI confirmation
                    _logger.LogInformation("Bust state set - score reverted to {Score}, waiting for UI confirmation", player.Score);
                }
                else
                {
                    game.CurrentTurn.Darts.Add(dart);
                    player.Score = newScore;
                    
                    if (newScore == 0)
                    {
                        // Leg won!
                        _logger.LogInformation("CHECKOUT! Player {Name} checked out with double {Seg}!", player.Name, dart.Segment);
                        player.LegsWon++;
                        game.LegWinnerId = player.Id;
                        
                        _logger.LogInformation("Player {Name} won leg {Leg}! ({LegsWon}/{LegsToWin})", 
                            player.Name, game.CurrentLeg, player.LegsWon, game.LegsToWin);
                        
                        if (player.LegsWon >= game.LegsToWin)
                        {
                            // Match won!
                            _logger.LogInformation("GAME WON by {Name}!", player.Name);
                            game.State = GameState.Finished;
                            game.WinnerId = player.Id;
                            game.EndedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            // Start next leg
                            StartNextLeg(game);
                        }
                    }
                }
                break;
        }

        // Turn is complete when 3 darts thrown, but DON'T auto-advance
        // Wait for DartSensor to signal "board cleared" before moving to next player
        // The UI will show the completed turn until player removes darts
    }

    /// <summary>
    /// End current turn and move to next player
    /// </summary>
    private void EndTurn(Game game)
    {
        var player = game.CurrentPlayer;
        if (player != null && game.CurrentTurn != null)
        {
            player.Turns.Add(game.CurrentTurn);
        }

        // Next player
        var prevIndex = game.CurrentPlayerIndex;
        game.CurrentPlayerIndex = (game.CurrentPlayerIndex + 1) % game.Players.Count;
        
        // Track rounds - a round completes when we wrap back to player 0
        // For single player: prevIndex == 0, so we just check if we wrapped
        if (game.CurrentPlayerIndex == 0)
        {
            // Only increment if this isn't the very first turn (prevIndex would be 0 at game start)
            // For multiplayer: wrap means everyone played. For single player: every turn is a round.
            if (game.Players.Count == 1 || prevIndex != 0)
            {
                game.CurrentRound++;
                _logger.LogInformation("Round {Round} starting", game.CurrentRound);
            }
        }
        
        game.CurrentTurn = new Turn
        {
            TurnNumber = game.CurrentPlayer?.Turns.Count + 1 ?? 1,
            PlayerId = game.CurrentPlayer?.Id ?? ""
        };
    }
    
    /// <summary>
    /// Manually advance to next player's turn (for "Next" button)
    /// </summary>
    public void NextTurn(Game game)
    {
        if (game == null || game.State != GameState.InProgress) return;
        
        // Clear known darts (player pulled their darts)
        game.KnownDarts.Clear();
        
        // End current turn and move to next player
        EndTurn(game);
        
        _logger.LogInformation("Manual next turn - now player {Index}: {Name}", 
            game.CurrentPlayerIndex, game.CurrentPlayer?.Name);
    }

    /// <summary>
    /// Confirm bust and end turn (called from UI after player acknowledges bust)
    /// </summary>
    public void ConfirmBust(Game game)
    {
        if (game == null || game.CurrentTurn == null) return;
        
        if (!game.CurrentTurn.IsBusted)
        {
            _logger.LogWarning("ConfirmBust called but turn is not busted");
            return;
        }
        
        _logger.LogInformation("Bust confirmed by UI - ending turn");
        
        // Clear known darts (player pulled their darts)
        game.KnownDarts.Clear();
        
        // Now end the turn
        EndTurn(game);
    }

    /// <summary>
    /// Start the next leg (reset scores, rotate starting player)
    /// </summary>
    private void StartNextLeg(Game game)
    {
        game.CurrentLeg++;
        game.LegWinnerId = null;  // Clear for next leg
        
        // Reset player scores for new leg
        foreach (var player in game.Players)
        {
            player.Score = game.Mode switch
            {
                GameMode.Game501 => 501,
                GameMode.Game301 => 301,
                _ => 0
            };
            player.Turns.Clear();
        }
        
        // Rotate starting player (loser of previous leg starts, or just rotate)
        game.CurrentPlayerIndex = (game.CurrentPlayerIndex + 1) % game.Players.Count;
        
        // Clear board
        game.KnownDarts.Clear();
        
        // Start new turn
        game.CurrentTurn = new Turn
        {
            TurnNumber = 1,
            PlayerId = game.CurrentPlayer?.Id ?? ""
        };
        
        _logger.LogInformation("Starting leg {Leg} of {TotalLegs}", game.CurrentLeg, game.TotalLegs);
    }

    /// <summary>
    /// Clear known darts (player pulled darts from board)
    /// If turn is complete (3 darts), also advance to next player
    /// </summary>
    public void ClearBoard(string boardId)
    {
        var game = GetGameForBoard(boardId);
        if (game != null)
        {
            game.KnownDarts.Clear();
            
            // If turn is complete (3 darts thrown), advance to next player
            if (game.CurrentTurn?.IsComplete == true && game.State == GameState.InProgress)
            {
                _logger.LogInformation("Turn complete, advancing to next player on board clear");
                EndTurn(game);
            }
            
            _logger.LogInformation("Board {BoardId} cleared", boardId);
        }
    }

    /// <summary>
    /// Apply a manually entered dart or a dart from DartDetect push notification.
    /// This is the main entry point for registering dart throws.
    /// </summary>
    public void ApplyManualDart(Game game, DartThrow dart)
    {
        ApplyDartToGame(game, dart);
        _logger.LogInformation("Dart applied: {Zone} = {Score} pts", dart.Zone, dart.Score);
    }

    /// <summary>
    /// Correct a dart in the current turn
    /// </summary>
    public void CorrectDart(Game game, int dartIndex, DartThrow newDart)
    {
        if (game.CurrentTurn == null || dartIndex >= game.CurrentTurn.Darts.Count)
            return;

        var player = game.CurrentPlayer;
        if (player == null) return;

        var oldDart = game.CurrentTurn.Darts[dartIndex];
        var scoreDiff = newDart.Score - oldDart.Score;

        // Replace the dart
        game.CurrentTurn.Darts[dartIndex] = newDart;

        // Adjust player score based on game mode
        switch (game.Mode)
        {
            case GameMode.Practice:
                player.Score += scoreDiff;
                break;

            case GameMode.Game501:
            case GameMode.Game301:
                // For X01 games, we subtract scores, so add the old and subtract the new
                // oldScore was subtracted, newScore needs to be subtracted instead
                player.Score = player.Score + oldDart.Score - newDart.Score;
                
                // Check for bust after correction
                if (player.Score < 0 || (player.Score == 1))
                {
                    // This would be a bust - but corrections shouldn't cause busts
                    // Just prevent going negative
                    player.Score = player.Score - oldDart.Score + newDart.Score; // Revert
                    game.CurrentTurn.Darts[dartIndex] = oldDart; // Revert dart
                    _logger.LogWarning("Correction would cause bust, reverting");
                    return;
                }
                
                // Check for checkout
                if (player.Score == 0 && newDart.Multiplier == 2)
                {
                    player.LegsWon++;
                    game.LegWinnerId = player.Id;
                    
                    if (player.LegsWon >= game.LegsToWin)
                    {
                        game.State = GameState.Finished;
                        game.WinnerId = player.Id;
                        game.EndedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        StartNextLeg(game);
                    }
                }
                
                // Check if correction clears a bust state
                if (game.CurrentTurn.IsBusted && player.Score > 1)
                {
                    game.CurrentTurn.IsBusted = false;
                    _logger.LogInformation("Correction cleared bust state, score now {Score}", player.Score);
                }
                break;
        }

        _logger.LogInformation("Corrected dart {Index}: {OldScore} -> {NewScore}, player score now {PlayerScore}", 
            dartIndex, oldDart.Score, newDart.Score, player.Score);
    }

    public DartThrow? RemoveDart(Game game, int dartIndex)
    {
        if (game.CurrentTurn == null || dartIndex < 0 || dartIndex >= game.CurrentTurn.Darts.Count)
            return null;

        var player = game.CurrentPlayer;
        if (player == null) return null;

        var removedDart = game.CurrentTurn.Darts[dartIndex];

        // Revert the score
        switch (game.Mode)
        {
            case GameMode.Practice:
                player.Score -= removedDart.Score;
                break;

            case GameMode.Game501:
            case GameMode.Game301:
                // X01: scores were subtracted, so add it back
                player.Score += removedDart.Score;
                break;
        }

        // Remove the dart from the turn
        game.CurrentTurn.Darts.RemoveAt(dartIndex);

        // Re-index remaining darts
        for (int i = dartIndex; i < game.CurrentTurn.Darts.Count; i++)
        {
            game.CurrentTurn.Darts[i].Index = i;
        }

        _logger.LogInformation("Removed false dart {Index}: {Zone}={Score}, player score now {PlayerScore}", 
            dartIndex, removedDart.Zone, removedDart.Score, player.Score);

        return removedDart;
    }

    #endregion
}
