using DartGameAPI.Models;

namespace DartGameAPI.Services;

/// <summary>
/// Cricket game engine supporting Standard and Cutthroat variants.
/// Target numbers: 20, 19, 18, 17, 16, 15, Bull (25)
/// </summary>
public class CricketGameEngine
{
    private readonly ILogger<CricketGameEngine> _logger;

    public static readonly int[] CricketNumbers = { 20, 19, 18, 17, 16, 15, 25 };

    public CricketGameEngine(ILogger<CricketGameEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start a new cricket match.
    /// </summary>
    public Game StartMatch(GameMode mode, List<string> playerNames, string boardId, int bestOf = 5)
    {
        bool isCutthroat = mode == GameMode.CricketCutthroat;
        int legsToWin = (bestOf / 2) + 1;

        var rules = new GameRules
        {
            DartsPerTurn = 3,
            StartingScore = 0,
            Direction = ScoringDirection.CountUp,
            ActiveSegments = new List<int> { 15, 16, 17, 18, 19, 20, 25 },
            DisplayName = isCutthroat ? "Cut-Throat Cricket" : "Cricket"
        };

        var game = new Game
        {
            BoardId = boardId,
            Mode = mode,
            State = GameState.InProgress,
            Rules = rules,
            EngineState = EngineState.MatchNotStarted,
            LegsToWin = legsToWin,
            DartsPerTurn = 3,
            CurrentLeg = 1,
            Players = playerNames.Select(name => new Player
            {
                Name = name,
                Score = 0,
                LegsWon = 0,
                IsIn = true
            }).ToList(),
            CricketState = new CricketState()
        };

        // Initialize cricket marks for each player
        foreach (var player in game.Players)
        {
            game.CricketState.Marks[player.Id] = new Dictionary<int, int>();
            foreach (var num in CricketNumbers)
                game.CricketState.Marks[player.Id][num] = 0;
        }

        game.CricketState.IsCutthroat = isCutthroat;

        _logger.LogInformation("Cricket match started: mode={Mode}, players={Count}, bestOf={BestOf}",
            mode, playerNames.Count, bestOf);

        return game;
    }

    /// <summary>
    /// Start a new leg within the match.
    /// </summary>
    public void StartLeg(Game game)
    {
        foreach (var player in game.Players)
        {
            player.Score = 0;
            player.Turns.Clear();
        }

        // Re-init cricket marks
        game.CricketState ??= new CricketState();
        game.CricketState.Marks.Clear();
        foreach (var player in game.Players)
        {
            game.CricketState.Marks[player.Id] = new Dictionary<int, int>();
            foreach (var num in CricketNumbers)
                game.CricketState.Marks[player.Id][num] = 0;
        }

        // Determine starting player (alternate by leg)
        game.CurrentPlayerIndex = (game.CurrentLeg - 1) % game.Players.Count;
        game.KnownDarts.Clear();
        game.PendingBusts.Clear();
        game.EngineState = EngineState.InLeg;

        StartTurn(game, game.CurrentPlayer!.Id);

        _logger.LogInformation("Cricket leg {Leg} started, player: {Player}",
            game.CurrentLeg, game.CurrentPlayer?.Name);
    }

    /// <summary>
    /// Start a turn for the given player.
    /// </summary>
    public void StartTurn(Game game, string playerId)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return;

        game.CurrentTurn = new Turn
        {
            TurnNumber = player.Turns.Count + 1,
            PlayerId = playerId,
            TurnStartScore = player.Score,
            IsTurnActive = true
        };

        game.EngineState = EngineState.InTurnAwaitingThrow;
    }

    /// <summary>
    /// Process a dart throw — the core cricket scoring logic.
    /// </summary>
    public DartResult ProcessDart(Game game, DartThrow dart)
    {
        var player = game.CurrentPlayer;
        if (player == null)
            return new DartResult { Type = DartResultType.Scored };

        var turn = game.CurrentTurn!;
        dart.Index = turn.Darts.Count;
        turn.Darts.Add(dart);
        player.DartsThrown++;

        var isCutthroat = game.CricketState?.IsCutthroat ?? false;

        // Determine if this dart hits a cricket number
        int segment = dart.Segment;
        int multiplier = dart.Multiplier;

        // Normalize bull: segment 25 with multiplier 1 or 2
        bool isCricketNumber = CricketNumbers.Contains(segment);

        if (isCricketNumber && segment != 0 && multiplier > 0)
        {
            int marks = multiplier; // S=1, D=2, T=3
            ApplyMarks(game, player, segment, marks, isCutthroat);
        }

        // Check for win
        if (CheckWin(game, player, isCutthroat))
        {
            player.LegsWon++;
            game.LegWinnerId = player.Id;

            if (player.LegsWon >= game.LegsToWin)
            {
                game.State = GameState.Finished;
                game.WinnerId = player.Id;
                game.EndedAt = DateTime.UtcNow;
                game.EngineState = EngineState.MatchEnded;
                return new DartResult { Type = DartResultType.MatchWon, ScoreAfter = player.Score };
            }

            game.EngineState = EngineState.LegEnded;
            return new DartResult { Type = DartResultType.LegWon, ScoreAfter = player.Score };
        }

        bool turnComplete = turn.Darts.Count >= game.DartsPerTurn;
        return new DartResult
        {
            Type = DartResultType.Scored,
            ScoreAfter = player.Score,
            TurnComplete = turnComplete
        };
    }

    private void ApplyMarks(Game game, Player player, int segment, int marks, bool isCutthroat)
    {
        var state = game.CricketState!;
        var playerMarks = state.Marks[player.Id];
        int currentMarks = playerMarks[segment];

        if (currentMarks >= 3)
        {
            // Already closed — score points if number is still open for others
            if (!IsNumberDeadForAll(state, segment, game.Players))
            {
                int pointsPerHit = segment == 25 ? 25 : segment;
                // For doubles/triples on bull: D-bull scores 50 (2*25), etc.
                // But marks represents multiplier, so we score marks * pointValue
                int totalPoints = marks * pointsPerHit;

                if (isCutthroat)
                {
                    // Points go to opponents who haven't closed this number
                    foreach (var opponent in game.Players)
                    {
                        if (opponent.Id == player.Id) continue;
                        if (state.Marks[opponent.Id][segment] < 3)
                        {
                            opponent.Score += totalPoints;
                            _logger.LogDebug("Cutthroat: {Opponent} gets {Points} on {Segment}",
                                opponent.Name, totalPoints, segment);
                        }
                    }
                }
                else
                {
                    // Standard: points go to the thrower
                    player.Score += totalPoints;
                    _logger.LogDebug("Standard: {Player} scores {Points} on {Segment}",
                        player.Name, totalPoints, segment);
                }
            }
            return;
        }

        // Apply marks toward closing
        int marksNeeded = 3 - currentMarks;
        int marksToClose = Math.Min(marks, marksNeeded);
        int excessMarks = marks - marksToClose;

        playerMarks[segment] = currentMarks + marksToClose;

        _logger.LogDebug("{Player}: {Segment} now at {Marks}/3 marks",
            player.Name, segment, playerMarks[segment]);

        // If just closed and there are excess marks, score them
        if (playerMarks[segment] >= 3 && excessMarks > 0)
        {
            if (!IsNumberDeadForAll(state, segment, game.Players))
            {
                int pointsPerHit = segment == 25 ? 25 : segment;
                int totalPoints = excessMarks * pointsPerHit;

                if (isCutthroat)
                {
                    foreach (var opponent in game.Players)
                    {
                        if (opponent.Id == player.Id) continue;
                        if (state.Marks[opponent.Id][segment] < 3)
                        {
                            opponent.Score += totalPoints;
                        }
                    }
                }
                else
                {
                    player.Score += totalPoints;
                }
            }
        }
    }

    private bool IsNumberDeadForAll(CricketState state, int segment, List<Player> players)
    {
        return players.All(p => state.Marks[p.Id][segment] >= 3);
    }

    private bool CheckWin(Game game, Player player, bool isCutthroat)
    {
        var state = game.CricketState!;
        var playerMarks = state.Marks[player.Id];

        // Must have all 7 numbers closed
        bool allClosed = CricketNumbers.All(n => playerMarks[n] >= 3);
        if (!allClosed) return false;

        if (isCutthroat)
        {
            // Lowest score wins (or equal)
            return game.Players.All(p => p.Id == player.Id || player.Score <= p.Score);
        }
        else
        {
            // Highest score wins (or equal)
            return game.Players.All(p => p.Id == player.Id || player.Score >= p.Score);
        }
    }

    /// <summary>
    /// Correct a dart — recalculates entire turn from scratch.
    /// </summary>
    public DartResult? CorrectDart(Game game, string playerId, int dartIndex, DartThrow correctedDart)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return null;

        var turn = game.CurrentTurn;
        if (turn == null || turn.PlayerId != playerId)
        {
            turn = player.Turns.LastOrDefault();
            if (turn == null) return null;
        }

        if (dartIndex < 0 || dartIndex >= turn.Darts.Count) return null;

        // Replace dart
        correctedDart.Index = dartIndex;
        turn.Darts[dartIndex] = correctedDart;

        // Recalculate the entire leg from scratch
        RecalculateFromScratch(game);

        return new DartResult
        {
            Type = DartResultType.Scored,
            ScoreAfter = player.Score
        };
    }

    /// <summary>
    /// Recalculate all marks and scores from the beginning of the leg.
    /// Used after corrections to ensure consistency.
    /// </summary>
    private void RecalculateFromScratch(Game game)
    {
        var state = game.CricketState!;
        var isCutthroat = state.IsCutthroat;

        // Reset all marks and scores
        foreach (var player in game.Players)
        {
            player.Score = 0;
            state.Marks[player.Id] = new Dictionary<int, int>();
            foreach (var num in CricketNumbers)
                state.Marks[player.Id][num] = 0;
        }

        // Replay all completed turns
        foreach (var player in game.Players)
        {
            foreach (var turn in player.Turns)
            {
                foreach (var dart in turn.Darts)
                {
                    ReplayDart(game, player, dart, isCutthroat);
                }
            }
        }

        // Replay current turn darts (interleaving doesn't matter for marks in same turn)
        if (game.CurrentTurn != null)
        {
            var currentPlayer = game.Players.FirstOrDefault(p => p.Id == game.CurrentTurn.PlayerId);
            if (currentPlayer != null)
            {
                foreach (var dart in game.CurrentTurn.Darts)
                {
                    ReplayDart(game, currentPlayer, dart, isCutthroat);
                }
            }
        }
    }

    private void ReplayDart(Game game, Player player, DartThrow dart, bool isCutthroat)
    {
        int segment = dart.Segment;
        int multiplier = dart.Multiplier;
        if (!CricketNumbers.Contains(segment) || segment == 0 || multiplier <= 0) return;
        ApplyMarks(game, player, segment, multiplier, isCutthroat);
    }

    /// <summary>
    /// Start the next leg.
    /// </summary>
    public void StartNextLeg(Game game)
    {
        game.CurrentLeg++;
        game.LegWinnerId = null;
        StartLeg(game);
    }
}
