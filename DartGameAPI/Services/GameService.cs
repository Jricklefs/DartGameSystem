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

    public Game CreateGame(string boardId, GameMode mode, List<string> playerNames, int bestOf = 5)
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
        game.CurrentTurn.Darts.Add(dart);
        player.DartsThrown++;

        // Apply score based on game mode
        switch (game.Mode)
        {
            case GameMode.Practice:
                player.Score += dart.Score;
                break;

            case GameMode.Game501:
            case GameMode.Game301:
                var newScore = player.Score - dart.Score;
                
                // Bust check
                if (newScore < 0 || (newScore == 0 && dart.Multiplier != 2))
                {
                    // Bust - revert turn
                    player.Score += game.CurrentTurn.Darts.Take(game.CurrentTurn.Darts.Count - 1).Sum(d => d.Score);
                    game.CurrentTurn.Darts.Clear();
                    game.CurrentTurn.Darts.Add(dart); // Keep bust dart for record
                    EndTurn(game);
                }
                else
                {
                    player.Score = newScore;
                    
                    if (newScore == 0)
                    {
                        // Leg won!
                        player.LegsWon++;
                        game.LegWinnerId = player.Id;
                        
                        _logger.LogInformation("Player {Name} won leg {Leg}! ({LegsWon}/{LegsToWin})", 
                            player.Name, game.CurrentLeg, player.LegsWon, game.LegsToWin);
                        
                        if (player.LegsWon >= game.LegsToWin)
                        {
                            // Match won!
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

        // Check if turn complete
        if (game.CurrentTurn.IsComplete && game.State == GameState.InProgress)
        {
            EndTurn(game);
        }
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
        if (game.CurrentPlayerIndex == 0 && prevIndex != 0)
        {
            game.CurrentRound++;
            _logger.LogInformation("Round {Round} starting", game.CurrentRound);
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
    /// Start the next leg (reset scores, rotate starting player)
    /// </summary>
    private void StartNextLeg(Game game)
    {
        game.CurrentLeg++;
        
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
    /// </summary>
    public void ClearBoard(string boardId)
    {
        var game = GetGameForBoard(boardId);
        if (game != null)
        {
            game.KnownDarts.Clear();
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
                break;
        }

        _logger.LogInformation("Corrected dart {Index}: {OldScore} -> {NewScore}, player score now {PlayerScore}", 
            dartIndex, oldDart.Score, newDart.Score, player.Score);
    }

    #endregion
}
