using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Result types for dart processing
/// </summary>
public enum DartResultType
{
    Scored,
    ConsumedNotIn,    // Double-In required, dart didn't qualify
    DoubleInActivated, // Player just got "in" with this dart
    Bust,
    LegWon,
    SetWon,
    MatchWon,
    TurnEnded
}

/// <summary>
/// Result of processing a single dart
/// </summary>
public class DartResult
{
    public DartResultType Type { get; set; }
    public int ScoreAfter { get; set; }
    public PendingBust? PendingBust { get; set; }
    public bool TurnComplete { get; set; }
    public string? BustReason { get; set; }
}

/// <summary>
/// Engine state machine states
/// </summary>
public enum EngineState
{
    MatchNotStarted,
    InLeg,
    InTurnAwaitingThrow,
    BustPending,
    CorrectionPending,
    LegEnded,
    SetEnded,
    MatchEnded
}

/// <summary>
/// Production-grade X01 game engine with full state machine.
/// Handles Double-In, Double-Out, Master-Out, Sets/Legs, bust management, and dart correction.
/// </summary>
public class X01GameEngine
{
    private readonly ILogger<X01GameEngine> _logger;
    private readonly IDartSensorController _sensor;
    private readonly GameEventDispatcher _events;

    public X01GameEngine(ILogger<X01GameEngine> logger, IDartSensorController sensor, GameEventDispatcher events)
    {
        _logger = logger;
        _sensor = sensor;
        _events = events;
    }

    // === Match State (stored on Game object via extension properties) ===
    // We use Game.MatchConfig, Game.EngineState, Game.PendingBust, Game.ActiveTurnState

    /// <summary>
    /// Start a new match with the given config and players.
    /// </summary>
    public Game StartMatch(MatchConfig config, List<string> playerNames, string boardId)
    {
        var rules = new GameRules
        {
            DartsPerTurn = config.DartsPerTurn,
            StartingScore = config.StartingScore,
            Direction = ScoringDirection.CountDown,
            RequireDoubleOut = config.DoubleOut || config.MasterOut,
            RequireDoubleIn = config.DoubleIn,
            MasterOut = config.MasterOut,
            DisplayName = $"X01 ({config.StartingScore})"
        };

        var game = new Game
        {
            BoardId = boardId,
            Mode = GameMode.X01,
            State = GameState.InProgress,
            Rules = rules,
            MatchConfig = config,
            EngineState = EngineState.MatchNotStarted,
            LegsToWin = config.SetsEnabled ? config.LegsPerSet : config.LegsToWin,
            RequireDoubleOut = rules.RequireDoubleOut,
            DartsPerTurn = config.DartsPerTurn,
            CurrentLeg = 1,
            Players = playerNames.Select(name => new Player
            {
                Name = name,
                Score = config.StartingScore,
                LegsWon = 0,
                SetsWon = 0,
                IsIn = !config.DoubleIn // If no DI required, player starts "in"
            }).ToList()
        };

        _logger.LogInformation("Match started: {StartingScore} DI={DI} DO={DO} MO={MO} Legs={Legs} Sets={Sets}",
            config.StartingScore, config.DoubleIn, config.DoubleOut, config.MasterOut,
            config.LegsToWin, config.SetsEnabled ? config.SetsToWin : 0);

        return game;
    }

    /// <summary>
    /// Start a new leg within the match. Resets scores, IsIn, determines starting player.
    /// </summary>
    public void StartLeg(Game game)
    {
        var config = game.MatchConfig ?? MatchConfig.FromLegacyMode(game.Mode);

        foreach (var player in game.Players)
        {
            player.Score = config.StartingScore;
            player.IsIn = !config.DoubleIn;
            player.Turns.Clear();
        }

        // Determine starting player
        DetermineStartingPlayer(game, config);

        game.KnownDarts.Clear();
        game.PendingBusts.Clear();
        game.EngineState = EngineState.InLeg;

        // Start the first turn
        StartTurn(game, game.CurrentPlayer!.Id);

        _logger.LogInformation("Leg {Leg} started, starting player: {Player}",
            game.CurrentLeg, game.CurrentPlayer?.Name);
    }

    /// <summary>
    /// Start a turn for the given player
    /// </summary>
    public void StartTurn(Game game, string playerId)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return;

        var turnNumber = player.Turns.Count + 1;
        game.CurrentTurn = new Turn
        {
            TurnNumber = turnNumber,
            PlayerId = playerId,
            TurnStartScore = player.Score,
            IsTurnActive = true,
            BustPending = false
        };

        game.EngineState = EngineState.InTurnAwaitingThrow;

        _logger.LogDebug("Turn started: player={Player}, turnStartScore={Score}", player.Name, player.Score);
    }

    /// <summary>
    /// Process a dart throw. This is the core scoring logic.
    /// </summary>
    public DartResult ProcessDart(Game game, DartThrow dart)
    {
        var player = game.CurrentPlayer;
        if (player == null)
            return new DartResult { Type = DartResultType.Scored };

        var config = game.MatchConfig ?? MatchConfig.FromLegacyMode(game.Mode);
        var turn = game.CurrentTurn!;

        // Set dart index and add to turn
        dart.Index = turn.Darts.Count;
        turn.Darts.Add(dart);
        player.DartsThrown++;
        var dartsThrown = turn.Darts.Count;

        // === DOUBLE-IN CHECK ===
        if (config.DoubleIn && !player.IsIn)
        {
            if (dart.Multiplier == 2) // Includes double bull (25x2=50)
            {
                player.IsIn = true;
                player.Score -= dart.Score;
                _logger.LogInformation("Player {Name} is IN with {Zone} ({Score})", player.Name, dart.Zone, dart.Score);

                // Now check if this double-in dart also checks out (e.g., starting score = 50, hit D25)
                if (player.Score == 0)
                {
                    return HandleCheckout(game, player, dart, config);
                }

                var result = new DartResult
                {
                    Type = DartResultType.DoubleInActivated,
                    ScoreAfter = player.Score,
                    TurnComplete = dartsThrown >= config.DartsPerTurn
                };
                return result;
            }
            else
            {
                // Dart consumed, no score change
                _logger.LogDebug("Player {Name} not in yet, dart consumed ({Zone})", player.Name, dart.Zone);
                var result = new DartResult
                {
                    Type = DartResultType.ConsumedNotIn,
                    ScoreAfter = player.Score,
                    TurnComplete = dartsThrown >= config.DartsPerTurn
                };
                return result;
            }
        }

        // === PLAYER IS IN — NORMAL SCORING ===
        var tentativeScore = player.Score - dart.Score;

        // Bust: negative
        if (tentativeScore < 0)
        {
            return HandleBust(game, player, dart, turn, "negative");
        }

        // Bust: score is 1 with double-out (can't check out from 1)
        if ((config.DoubleOut || config.MasterOut) && tentativeScore == 1)
        {
            return HandleBust(game, player, dart, turn, "score_is_1");
        }

        // Checkout attempt: score is 0
        if (tentativeScore == 0)
        {
            bool validCheckout;
            if (config.MasterOut)
                validCheckout = dart.Multiplier == 2 || dart.Multiplier == 3;
            else if (config.DoubleOut)
                validCheckout = dart.Multiplier == 2;
            else
                validCheckout = true; // Straight out

            if (!validCheckout)
            {
                return HandleBust(game, player, dart, turn, "invalid_checkout");
            }

            // Valid checkout!
            player.Score = 0;
            return HandleCheckout(game, player, dart, config);
        }

        // Normal score
        player.Score = tentativeScore;
        _logger.LogDebug("Dart scored: {Zone}={Score}, player {Name} now at {PlayerScore}",
            dart.Zone, dart.Score, player.Name, player.Score);

        var dartResult = new DartResult
        {
            Type = DartResultType.Scored,
            ScoreAfter = player.Score,
            TurnComplete = dartsThrown >= config.DartsPerTurn
        };

        return dartResult;
    }

    /// <summary>
    /// Confirm a pending bust. Reverts score to turn start and ends the turn.
    /// </summary>
    public void ConfirmBust(Game game, string pendingBustId)
    {
        var bust = game.PendingBusts.FirstOrDefault(b => b.Id == pendingBustId);
        if (bust == null)
        {
            _logger.LogWarning("ConfirmBust: pending bust {Id} not found", pendingBustId);
            return;
        }

        var player = game.Players.FirstOrDefault(p => p.Id == bust.PlayerId);
        if (player == null) return;

        // Score was already reverted when bust was detected; just confirm and end turn
        player.Score = bust.TurnStartScore;
        game.CurrentTurn!.IsBusted = true;
        game.CurrentTurn!.BustConfirmed = true;
        game.PendingBusts.Remove(bust);
        game.EngineState = EngineState.InTurnAwaitingThrow; // Will transition on EndTurn

        _logger.LogInformation("Bust confirmed for player {Name}, score reverted to {Score}",
            player.Name, player.Score);
    }

    /// <summary>
    /// Override a bust with a corrected dart. Recomputes from turn start.
    /// </summary>
    public DartResult OverrideBustWithCorrectedDart(Game game, string pendingBustId, DartThrow correctedDart)
    {
        var bust = game.PendingBusts.FirstOrDefault(b => b.Id == pendingBustId);
        if (bust == null)
        {
            _logger.LogWarning("OverrideBust: pending bust {Id} not found", pendingBustId);
            return new DartResult { Type = DartResultType.Scored };
        }

        var player = game.Players.FirstOrDefault(p => p.Id == bust.PlayerId);
        if (player == null)
            return new DartResult { Type = DartResultType.Scored };

        var config = game.MatchConfig ?? MatchConfig.FromLegacyMode(game.Mode);
        var turn = game.CurrentTurn!;

        // Replace the bust dart with corrected dart
        if (bust.DartIndex < turn.Darts.Count)
        {
            turn.Darts[bust.DartIndex] = correctedDart;
            correctedDart.Index = bust.DartIndex;
        }

        // Recompute score from turn start
        player.Score = bust.TurnStartScore;
        player.IsIn = !config.DoubleIn || player.IsIn; // Keep IsIn if already in

        // Replay all darts from turn start
        // We need to temporarily remove and re-add darts
        var darts = turn.Darts.ToList();
        turn.Darts.Clear();
        player.DartsThrown -= darts.Count; // Will be re-incremented

        // Reset turn state
        turn.IsBusted = false;
        turn.BustPending = false;
        turn.BustConfirmed = false;

        game.PendingBusts.Remove(bust);
        game.EngineState = EngineState.InTurnAwaitingThrow;

        DartResult lastResult = new DartResult { Type = DartResultType.Scored };
        foreach (var d in darts)
        {
            lastResult = ProcessDart(game, d);
            if (lastResult.Type == DartResultType.Bust)
            {
                // Still busts with corrected dart — confirm automatically
                var newBust = game.PendingBusts.LastOrDefault();
                if (newBust != null)
                {
                    ConfirmBust(game, newBust.Id);
                }
                return lastResult;
            }
        }

        // If we get here, the corrected dart didn't bust
        _logger.LogInformation("Bust overridden: player {Name} now at {Score}", player.Name, player.Score);

        return lastResult;
    }

    /// <summary>
    /// Correct a dart in the current or most recent turn (Strategy A).
    /// Recomputes from turn start score.
    /// </summary>
    public DartResult? CorrectDart(Game game, string turnPlayerId, int dartIndex, DartThrow correctedDart)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == turnPlayerId);
        if (player == null) return null;

        var config = game.MatchConfig ?? MatchConfig.FromLegacyMode(game.Mode);
        var turn = game.CurrentTurn;
        
        // Check if correcting current turn
        if (turn == null || turn.PlayerId != turnPlayerId)
        {
            // Check most recent completed turn for this player
            turn = player.Turns.LastOrDefault();
            if (turn == null) return null;
        }

        if (dartIndex < 0 || dartIndex >= turn.Darts.Count) return null;

        var oldDart = turn.Darts[dartIndex];

        // Replace dart
        correctedDart.Index = dartIndex;
        turn.Darts[dartIndex] = correctedDart;

        // Recompute from turn start
        var startScore = turn.TurnStartScore;
        player.Score = startScore;

        // Re-check IsIn state from before this turn
        if (config.DoubleIn)
        {
            // Check if player was In before this turn by looking at turn start conditions
            // For simplicity, if turn start score < starting score, player was already in
            player.IsIn = startScore < config.StartingScore || !config.DoubleIn;
        }

        DartResult? lastResult = null;
        foreach (var d in turn.Darts)
        {
            // Simplified recompute — apply each dart's scoring
            if (config.DoubleIn && !player.IsIn)
            {
                if (d.Multiplier == 2)
                {
                    player.IsIn = true;
                    player.Score -= d.Score;
                }
                // else consumed
            }
            else
            {
                var tentative = player.Score - d.Score;
                if (tentative < 0 || ((config.DoubleOut || config.MasterOut) && tentative == 1))
                {
                    // Bust on recompute — revert
                    player.Score = startScore;
                    turn.IsBusted = true;
                    lastResult = new DartResult { Type = DartResultType.Bust, ScoreAfter = player.Score, BustReason = "correction_caused_bust" };
                    break;
                }
                else if (tentative == 0)
                {
                    bool valid = config.MasterOut ? (d.Multiplier == 2 || d.Multiplier == 3)
                               : config.DoubleOut ? d.Multiplier == 2
                               : true;
                    if (!valid)
                    {
                        player.Score = startScore;
                        turn.IsBusted = true;
                        lastResult = new DartResult { Type = DartResultType.Bust, ScoreAfter = player.Score, BustReason = "invalid_checkout" };
                        break;
                    }
                    player.Score = 0;
                    lastResult = new DartResult { Type = DartResultType.LegWon, ScoreAfter = 0 };
                    break;
                }
                else
                {
                    player.Score = tentative;
                }
            }
            lastResult = new DartResult { Type = DartResultType.Scored, ScoreAfter = player.Score };
        }

        // Clear bust state if recompute was clean
        if (lastResult?.Type == DartResultType.Scored || lastResult?.Type == DartResultType.LegWon)
        {
            turn.IsBusted = false;
            turn.BustPending = false;
        }

        _logger.LogInformation("Dart corrected: index={Index}, old={OldScore}, new={NewScore}, playerScore={PlayerScore}",
            dartIndex, oldDart.Score, correctedDart.Score, player.Score);

        return lastResult;
    }

    // === Private helpers ===

    private DartResult HandleBust(Game game, Player player, DartThrow dart, Turn turn, string reason)
    {
        var bustScore = turn.TurnStartScore;

        // Revert score to turn start
        player.Score = bustScore;

        var pendingBust = new PendingBust
        {
            PlayerId = player.Id,
            TurnId = turn.TurnNumber.ToString(),
            TurnStartScore = bustScore,
            DartIndex = dart.Index,
            OriginalDart = dart,
            Reason = reason
        };

        game.PendingBusts.Add(pendingBust);
        turn.IsBusted = true;
        turn.BustPending = true;
        turn.ScoreBeforeBust = bustScore;
        game.EngineState = EngineState.BustPending;

        _logger.LogInformation("BUST: player={Name}, reason={Reason}, revert to {Score}",
            player.Name, reason, bustScore);

        return new DartResult
        {
            Type = DartResultType.Bust,
            ScoreAfter = bustScore,
            PendingBust = pendingBust,
            BustReason = reason
        };
    }

    private DartResult HandleCheckout(Game game, Player player, DartThrow dart, MatchConfig config)
    {
        player.LegsWon++;
        game.LegWinnerId = player.Id;

        _logger.LogInformation("CHECKOUT! Player {Name} won leg {Leg} ({LegsWon}/{LegsToWin})",
            player.Name, game.CurrentLeg, player.LegsWon,
            config.SetsEnabled ? config.LegsPerSet : config.LegsToWin);

        if (config.SetsEnabled)
        {
            // Check set win
            if (player.LegsWon >= config.LegsPerSet)
            {
                player.SetsWon++;
                // Reset legs for all players for new set
                foreach (var p in game.Players)
                    p.LegsWon = 0;

                if (player.SetsWon >= config.SetsToWin)
                {
                    // Match won
                    game.State = GameState.Finished;
                    game.WinnerId = player.Id;
                    game.EndedAt = DateTime.UtcNow;
                    game.EngineState = EngineState.MatchEnded;
                    return new DartResult { Type = DartResultType.MatchWon, ScoreAfter = 0 };
                }
                else
                {
                    game.EngineState = EngineState.SetEnded;
                    return new DartResult { Type = DartResultType.SetWon, ScoreAfter = 0 };
                }
            }
        }
        else
        {
            // No sets — check match win by legs
            if (player.LegsWon >= config.LegsToWin)
            {
                game.State = GameState.Finished;
                game.WinnerId = player.Id;
                game.EndedAt = DateTime.UtcNow;
                game.EngineState = EngineState.MatchEnded;
                return new DartResult { Type = DartResultType.MatchWon, ScoreAfter = 0 };
            }
        }

        // Leg won but match continues
        game.EngineState = EngineState.LegEnded;
        return new DartResult { Type = DartResultType.LegWon, ScoreAfter = 0 };
    }

    private void EndTurn(Game game)
    {
        var player = game.CurrentPlayer;
        if (player != null && game.CurrentTurn != null)
        {
            game.CurrentTurn.IsTurnActive = false;
            player.Turns.Add(game.CurrentTurn);
        }

        // Next player
        var prevIndex = game.CurrentPlayerIndex;
        game.CurrentPlayerIndex = (game.CurrentPlayerIndex + 1) % game.Players.Count;

        // Track rounds
        if (game.CurrentPlayerIndex == 0 && (game.Players.Count == 1 || prevIndex != 0))
        {
            game.CurrentRound++;
        }

        // Start next turn
        if (game.CurrentPlayer != null)
        {
            StartTurn(game, game.CurrentPlayer.Id);
        }
    }

    private void DetermineStartingPlayer(Game game, MatchConfig config)
    {
        switch (config.StartingPlayerRule)
        {
            case StartingPlayerRule.Alternate:
                // Rotate based on leg number (0-indexed)
                game.CurrentPlayerIndex = (game.CurrentLeg - 1) % game.Players.Count;
                break;

            case StartingPlayerRule.WinnerStarts:
                if (game.LegWinnerId != null)
                {
                    var winnerIdx = game.Players.FindIndex(p => p.Id == game.LegWinnerId);
                    if (winnerIdx >= 0) game.CurrentPlayerIndex = winnerIdx;
                }
                break;

            case StartingPlayerRule.FixedRotation:
                // Same as alternate but uses a fixed rotation counter
                game.CurrentPlayerIndex = (game.CurrentLeg - 1) % game.Players.Count;
                break;
        }
    }

    /// <summary>
    /// Start the next leg (called externally after LegEnded/SetEnded state)
    /// </summary>
    public void StartNextLeg(Game game)
    {
        game.CurrentLeg++;
        game.LegWinnerId = null;
        StartLeg(game);
    }
}
