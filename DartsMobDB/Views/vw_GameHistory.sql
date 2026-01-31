/*
================================================================================
    vw_GameHistory - Game history with player details
================================================================================
    User Story: As a player, I can view my game history
*/

CREATE VIEW [dbo].[vw_GameHistory]
AS
SELECT 
    g.GameId,
    g.BoardId,
    b.Name AS BoardName,
    g.GameMode,
    CASE g.GameMode
        WHEN 0 THEN 'Practice'
        WHEN 1 THEN '501'
        WHEN 2 THEN '301'
        WHEN 3 THEN 'Cricket'
        ELSE 'Unknown'
    END AS GameModeName,
    g.GameState,
    g.StartedAt,
    g.EndedAt,
    g.DurationSeconds,
    g.TotalDarts,
    
    -- Winner info
    g.WinnerPlayerId,
    wp.Nickname AS WinnerName,
    
    -- Player list (comma-separated)
    (
        SELECT STRING_AGG(p2.Nickname, ', ') WITHIN GROUP (ORDER BY gp2.PlayerOrder)
        FROM [dbo].[GamePlayers] gp2
        JOIN [dbo].[Players] p2 ON gp2.PlayerId = p2.PlayerId
        WHERE gp2.GameId = g.GameId
    ) AS PlayerNames,
    
    -- Player count
    (
        SELECT COUNT(*) 
        FROM [dbo].[GamePlayers] gp3 
        WHERE gp3.GameId = g.GameId
    ) AS PlayerCount

FROM [dbo].[Games] g
JOIN [dbo].[Boards] b ON g.BoardId = b.BoardId
LEFT JOIN [dbo].[Players] wp ON g.WinnerPlayerId = wp.PlayerId;
GO
