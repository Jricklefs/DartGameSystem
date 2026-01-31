/*
================================================================================
    vw_PlayerStats - Lifetime statistics per player
================================================================================
    User Story: As a player, I can see my lifetime stats
*/

CREATE VIEW [dbo].[vw_PlayerStats]
AS
SELECT 
    p.PlayerId,
    p.Nickname,
    
    -- Game counts
    COUNT(DISTINCT gp.GameId) AS GamesPlayed,
    SUM(CASE WHEN gp.IsWinner = 1 THEN 1 ELSE 0 END) AS GamesWon,
    
    -- Win rate
    CASE 
        WHEN COUNT(DISTINCT gp.GameId) > 0 
        THEN CAST(SUM(CASE WHEN gp.IsWinner = 1 THEN 1 ELSE 0 END) AS DECIMAL(5,2)) 
             / COUNT(DISTINCT gp.GameId) * 100
        ELSE 0 
    END AS WinRate,
    
    -- Dart stats
    SUM(gp.DartsThrown) AS TotalDartsThrown,
    MAX(gp.HighestTurn) AS BestTurnEver,
    
    -- Average per-game stats
    AVG(CAST(gp.DartsThrown AS DECIMAL(10,2))) AS AvgDartsPerGame,
    
    -- Recent activity
    MAX(g.StartedAt) AS LastGameAt

FROM [dbo].[Players] p
LEFT JOIN [dbo].[GamePlayers] gp ON p.PlayerId = gp.PlayerId
LEFT JOIN [dbo].[Games] g ON gp.GameId = g.GameId AND g.GameState = 1  -- Finished only
WHERE p.IsActive = 1
GROUP BY p.PlayerId, p.Nickname;
GO
