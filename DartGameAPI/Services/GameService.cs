using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Manages game state in memory. Delegates X01 scoring to X01GameEngine.
/// </summary>
public class GameService
{
    private readonly Dictionary<string, Board> _boards = new();
    private readonly Dictionary<string, Game> _games = new();
    private readonly ILogger<GameService> _logger;
    private readonly X01GameEngine _x01Engine;

    private const double POSITION_TOLERANCE_MM = 20.0;

    public GameService(ILogger<GameService> logger, X01GameEngine x01Engine)
    {
        _logger = logger;
        _x01Engine = x01Engine;
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

    /// <summary>
    /// Create a game using legacy parameters. Maps to X01 engine for X01 modes.
    /// </summary>
    public Game CreateGame(string boardId, GameMode mode, List<string> playerNames, int bestOf = 5, bool requireDoubleOut = false)
    {
        var board = GetBoard(boardId);
        if (board == null)
            throw new InvalidOperationException($"Board '{boardId}' not found");

        Game game;

        // Use X01 engine for all X01-type games
        if (mode == GameMode.Game501 || mode == GameMode.Game301 || mode == GameMode.Debug20 || mode == GameMode.X01)
        {
            var config = MatchConfig.FromLegacyMode(mode, requireDoubleOut, bestOf);
            game = _x01Engine.StartMatch(config, playerNames, boardId);
            // Map legacy mode onto the game for backward compat
            game.Mode = mode;
            _x01Engine.StartLeg(game);
        }
        else
        {
            // Practice / Cricket — original logic
            int legsToWin = (bestOf / 2) + 1;
            var rules = GameRules.FromMode(mode, requireDoubleOut);

            game = new Game
            {
                BoardId = boardId,
                Mode = mode,
                State = GameState.InProgress,
                LegsToWin = legsToWin,
                Rules = rules,
                RequireDoubleOut = rules.RequireDoubleOut,
                DartsPerTurn = rules.DartsPerTurn,
                CurrentLeg = 1,
                Players = playerNames.Select(name => new Player
                {
                    Name = name,
                    Score = rules.StartingScore,
                    LegsWon = 0
                }).ToList()
            };

            if (game.Players.Any())
            {
                game.CurrentTurn = new Turn
                {
                    TurnNumber = 1,
                    PlayerId = game.Players[0].Id
                };
            }
        }

        _games[game.Id] = game;
        board.CurrentGameId = game.Id;

        _logger.LogInformation("Created game {GameId} on board {BoardId}, mode {Mode}", game.Id, boardId, mode);
        return game;
    }

    /// <summary>
    /// Create a game with full MatchConfig (new API).
    /// </summary>
    public Game CreateGameWithConfig(string boardId, MatchConfig config, List<string> playerNames)
    {
        var board = GetBoard(boardId);
        if (board == null)
            throw new InvalidOperationException($"Board '{boardId}' not found");

        var game = _x01Engine.StartMatch(config, playerNames, boardId);
        _x01Engine.StartLeg(game);

        _games[game.Id] = game;
        board.CurrentGameId = game.Id;

        _logger.LogInformation("Created X01 game {GameId} on board {BoardId}, score={Score} DI={DI} DO={DO} MO={MO}",
            game.Id, boardId, config.StartingScore, config.DoubleIn, config.DoubleOut, config.MasterOut);
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
    /// Apply dart score to game state. Delegates to X01 engine for X01 games.
    /// </summary>
    private void ApplyDartToGame(Game game, DartThrow dart)
    {
        var player = game.CurrentPlayer;
        if (player == null) return;

        // Use X01 engine for all X01-type games
        if (game.IsX01Engine)
        {
            var result = _x01Engine.ProcessDart(game, dart);
            _logger.LogInformation("X01 engine result: {Type}, score={Score}", result.Type, result.ScoreAfter);
            return;
        }

        // === Legacy logic for Practice mode ===
        game.CurrentTurn ??= new Turn
        {
            TurnNumber = player.Turns.Count + 1,
            PlayerId = player.Id
        };
        player.DartsThrown++;

        switch (game.Mode)
        {
            case GameMode.Practice:
                game.CurrentTurn.Darts.Add(dart);
                player.Score += dart.Score;
                break;
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

        var prevIndex = game.CurrentPlayerIndex;
        game.CurrentPlayerIndex = (game.CurrentPlayerIndex + 1) % game.Players.Count;

        if (game.CurrentPlayerIndex == 0)
        {
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
    /// Manually advance to next player's turn
    /// </summary>
    public void NextTurn(Game game)
    {
        if (game == null || game.State != GameState.InProgress) return;
        game.KnownDarts.Clear();

        if (game.IsX01Engine)
        {
            // For X01 engine games, the engine manages turn state internally via ProcessDart.
            // NextTurn is called externally (board clear / next button) so we just
            // ensure the current turn is archived and a new one starts.
            var player = game.CurrentPlayer;
            if (player != null && game.CurrentTurn != null && game.CurrentTurn.IsTurnActive)
            {
                game.CurrentTurn.IsTurnActive = false;
                player.Turns.Add(game.CurrentTurn);
            }

            var prevIndex = game.CurrentPlayerIndex;
            game.CurrentPlayerIndex = (game.CurrentPlayerIndex + 1) % game.Players.Count;

            if (game.CurrentPlayerIndex == 0 && (game.Players.Count == 1 || prevIndex != 0))
            {
                game.CurrentRound++;
            }

            if (game.CurrentPlayer != null)
            {
                _x01Engine.StartTurn(game, game.CurrentPlayer.Id);
            }
        }
        else
        {
            EndTurn(game);
        }

        _logger.LogInformation("Next turn - player {Index}: {Name}",
            game.CurrentPlayerIndex, game.CurrentPlayer?.Name);
    }

    /// <summary>
    /// Confirm bust and end turn
    /// </summary>
    public void ConfirmBust(Game game)
    {
        if (game == null || game.CurrentTurn == null) return;

        if (game.IsX01Engine)
        {
            var pendingBust = game.PendingBusts.FirstOrDefault();
            if (pendingBust != null)
            {
                _x01Engine.ConfirmBust(game, pendingBust.Id);
                return;
            }
        }

        if (!game.CurrentTurn.IsBusted)
        {
            _logger.LogWarning("ConfirmBust called but turn is not busted");
            return;
        }

        game.CurrentTurn.BustConfirmed = true;
        _logger.LogInformation("Bust confirmed by UI");
    }

    /// <summary>
    /// Start next leg (for X01 engine games after LegEnded state)
    /// </summary>
    public void StartNextLeg(Game game)
    {
        if (game.IsX01Engine)
        {
            _x01Engine.StartNextLeg(game);
            return;
        }

        // Legacy leg start
        game.CurrentLeg++;
        foreach (var player in game.Players)
        {
            player.Score = game.Rules.StartingScore;
            player.Turns.Clear();
        }
        game.CurrentPlayerIndex = (game.CurrentPlayerIndex + 1) % game.Players.Count;
        game.KnownDarts.Clear();
        game.CurrentTurn = new Turn
        {
            TurnNumber = 1,
            PlayerId = game.CurrentPlayer?.Id ?? ""
        };
        _logger.LogInformation("Starting leg {Leg}", game.CurrentLeg);
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
    /// Apply a manually entered dart or a dart from detection.
    /// Main entry point for registering dart throws.
    /// </summary>
    public DartResult? ApplyManualDart(Game game, DartThrow dart)
    {
        game.LegWinnerId = null;

        if (game.IsX01Engine)
        {
            var result = _x01Engine.ProcessDart(game, dart);
            _logger.LogInformation("Dart applied via X01 engine: {Zone} = {Score} pts, result={Type}",
                dart.Zone, dart.Score, result.Type);
            return result;
        }

        ApplyDartToGame(game, dart);
        _logger.LogInformation("Dart applied: {Zone} = {Score} pts", dart.Zone, dart.Score);
        return null;
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

        if (game.IsX01Engine)
        {
            _x01Engine.CorrectDart(game, player.Id, dartIndex, newDart);
            return;
        }

        // Legacy correction for Practice
        var oldDart = game.CurrentTurn.Darts[dartIndex];
        var scoreDiff = newDart.Score - oldDart.Score;
        game.CurrentTurn.Darts[dartIndex] = newDart;

        switch (game.Mode)
        {
            case GameMode.Practice:
                player.Score += scoreDiff;
                break;
        }

        _logger.LogInformation("Corrected dart {Index}: {OldScore} -> {NewScore}", dartIndex, oldDart.Score, newDart.Score);
    }

    public DartThrow? RemoveDart(Game game, int dartIndex)
    {
        if (game.CurrentTurn == null || dartIndex < 0 || dartIndex >= game.CurrentTurn.Darts.Count)
            return null;

        var player = game.CurrentPlayer;
        if (player == null) return null;

        var removedDart = game.CurrentTurn.Darts[dartIndex];

        switch (game.Mode)
        {
            case GameMode.Practice:
                player.Score -= removedDart.Score;
                break;

            case GameMode.Game501:
            case GameMode.Game301:
            case GameMode.Debug20:
            case GameMode.X01:
                player.Score += removedDart.Score;
                break;
        }

        game.CurrentTurn.Darts.RemoveAt(dartIndex);
        for (int i = dartIndex; i < game.CurrentTurn.Darts.Count; i++)
            game.CurrentTurn.Darts[i].Index = i;

        _logger.LogInformation("Removed dart {Index}: {Zone}={Score}", dartIndex, removedDart.Zone, removedDart.Score);
        return removedDart;
    }

    #endregion
}
