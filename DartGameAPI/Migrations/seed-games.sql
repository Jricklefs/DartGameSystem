-- Seed fake games and stats for testing
SET QUOTED_IDENTIFIER ON;
GO

USE DartsMobDB;
GO

-- Get fake player IDs
DECLARE @fakePlayers TABLE (PlayerId UNIQUEIDENTIFIER, Nickname NVARCHAR(100), RowNum INT);
INSERT INTO @fakePlayers
SELECT PlayerId, Nickname, ROW_NUMBER() OVER (ORDER BY Nickname) as RowNum
FROM Players WHERE Email LIKE '%@dartsmob.fake';

-- Get a board ID (or create one)
DECLARE @boardId UNIQUEIDENTIFIER;
SELECT TOP 1 @boardId = BoardId FROM Boards;
IF @boardId IS NULL
BEGIN
    SET @boardId = NEWID();
    INSERT INTO Boards (BoardId, Name, CreatedAt) VALUES (@boardId, 'Test Board', GETUTCDATE());
END;

-- Create fake games between random pairs of players
DECLARE @i INT = 1;
DECLARE @gameId UNIQUEIDENTIFIER;
DECLARE @player1 UNIQUEIDENTIFIER;
DECLARE @player2 UNIQUEIDENTIFIER;
DECLARE @winner UNIQUEIDENTIFIER;
DECLARE @gameDate DATETIME2;
DECLARE @p1Score INT, @p2Score INT;
DECLARE @duration INT;
DECLARE @totalDarts INT;

WHILE @i <= 150  -- Create 150 fake games
BEGIN
    SET @gameId = NEWID();
    
    -- Pick two different random players
    SELECT TOP 1 @player1 = PlayerId FROM @fakePlayers ORDER BY NEWID();
    SELECT TOP 1 @player2 = PlayerId FROM @fakePlayers WHERE PlayerId != @player1 ORDER BY NEWID();
    
    -- Random game date in past 60 days
    SET @gameDate = DATEADD(MINUTE, -ABS(CHECKSUM(NEWID())) % 86400, DATEADD(DAY, -ABS(CHECKSUM(NEWID())) % 60, GETUTCDATE()));
    
    -- Random duration 10-45 minutes
    SET @duration = 600 + ABS(CHECKSUM(NEWID())) % 2100;
    
    -- Random total darts 30-120
    SET @totalDarts = 30 + ABS(CHECKSUM(NEWID())) % 90;
    
    -- Random scores (best of 3 or 5)
    SET @p1Score = 1 + ABS(CHECKSUM(NEWID())) % 3;  -- 1, 2, or 3
    SET @p2Score = CASE WHEN @p1Score = 3 THEN ABS(CHECKSUM(NEWID())) % 3 ELSE 3 END;  -- Loser gets 0-2
    
    -- Determine winner
    SET @winner = CASE WHEN @p1Score > @p2Score THEN @player1 ELSE @player2 END;
    
    -- Insert game
    INSERT INTO Games (GameId, BoardId, GameMode, GameState, StartedAt, EndedAt, WinnerPlayerId, TotalDarts, DurationSeconds)
    VALUES (
        @gameId,
        @boardId,
        '501',
        2,  -- Completed state (assuming 0=Setup, 1=InProgress, 2=Completed)
        @gameDate,
        DATEADD(SECOND, @duration, @gameDate),
        @winner,
        @totalDarts,
        @duration
    );
    
    -- Insert game players with stats
    INSERT INTO GamePlayers (GamePlayerId, GameId, PlayerId, PlayerOrder, StartingScore, FinalScore, DartsThrown, HighestTurn, IsWinner)
    VALUES 
        (NEWID(), @gameId, @player1, 1, 501, 
         CASE WHEN @winner = @player1 THEN 0 ELSE 50 + ABS(CHECKSUM(NEWID())) % 200 END,
         @totalDarts / 2 + ABS(CHECKSUM(NEWID())) % 10,
         60 + ABS(CHECKSUM(NEWID())) % 121,  -- Highest turn 60-180
         CASE WHEN @winner = @player1 THEN 1 ELSE 0 END),
        (NEWID(), @gameId, @player2, 2, 501, 
         CASE WHEN @winner = @player2 THEN 0 ELSE 50 + ABS(CHECKSUM(NEWID())) % 200 END,
         @totalDarts / 2 + ABS(CHECKSUM(NEWID())) % 10,
         60 + ABS(CHECKSUM(NEWID())) % 121,
         CASE WHEN @winner = @player2 THEN 1 ELSE 0 END);
    
    SET @i = @i + 1;
END;
GO

-- Update player ratings based on fake games
UPDATE pr SET
    GamesPlayed = stats.GameCount,
    Wins = stats.Wins,
    Losses = stats.GameCount - stats.Wins,
    Rating = 1000 + (stats.Wins * 15) - ((stats.GameCount - stats.Wins) * 10),
    AverageScore = stats.AvgHighTurn,
    CheckoutPercentage = 15.0 + (ABS(CHECKSUM(NEWID())) % 400) / 10.0,
    Highest180s = ABS(CHECKSUM(pr.PlayerId)) % 30,
    UpdatedAt = GETUTCDATE()
FROM PlayerRatings pr
INNER JOIN (
    SELECT 
        gp.PlayerId,
        COUNT(*) as GameCount,
        SUM(CAST(gp.IsWinner as INT)) as Wins,
        AVG(CAST(gp.HighestTurn as FLOAT)) as AvgHighTurn
    FROM GamePlayers gp
    INNER JOIN Players p ON gp.PlayerId = p.PlayerId
    WHERE p.Email LIKE '%@dartsmob.fake'
    GROUP BY gp.PlayerId
) stats ON pr.PlayerId = stats.PlayerId;
GO

-- Show summary
SELECT 'Games Created' as Metric, COUNT(*) as Value FROM Games WHERE GameMode = '501'
UNION ALL
SELECT 'Game Players', COUNT(*) FROM GamePlayers
UNION ALL
SELECT 'Avg Games Per Player', AVG(cnt) FROM (SELECT COUNT(*) cnt FROM GamePlayers GROUP BY PlayerId) t
UNION ALL
SELECT 'Player Ratings Updated', COUNT(*) FROM PlayerRatings WHERE GamesPlayed > 0;

PRINT 'Fake game data seeded successfully!';
