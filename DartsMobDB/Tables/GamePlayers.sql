/*
================================================================================
    GAMEPLAYERS - Players in each game (junction table)
================================================================================
    User Story: As a host, I can start a game and pick players
    User Story: As a player, I can view my game history
    
    Notes:
    - Links Players to Games (many-to-many)
    - PlayerOrder determines turn sequence
    - FinalScore is their score when game ended
    - For 501/301: FinalScore = remaining (0 = winner)
    - For Practice: FinalScore = total scored
*/

CREATE TABLE [dbo].[GamePlayers]
(
    [GamePlayerId]  UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [GameId]        UNIQUEIDENTIFIER    NOT NULL,
    [PlayerId]      UNIQUEIDENTIFIER    NOT NULL,
    [PlayerOrder]   INT                 NOT NULL,       -- 0-based turn order
    [StartingScore] INT                 NOT NULL,       -- 501, 301, or 0 for practice
    [FinalScore]    INT                 NULL,           -- Score when game ended
    [DartsThrown]   INT                 NOT NULL    DEFAULT 0,
    [HighestTurn]   INT                 NULL,           -- Best 3-dart turn score
    [IsWinner]      BIT                 NOT NULL    DEFAULT 0,

    CONSTRAINT [PK_GamePlayers] PRIMARY KEY CLUSTERED ([GamePlayerId]),
    CONSTRAINT [FK_GamePlayers_Games] FOREIGN KEY ([GameId]) REFERENCES [dbo].[Games]([GameId]),
    CONSTRAINT [FK_GamePlayers_Players] FOREIGN KEY ([PlayerId]) REFERENCES [dbo].[Players]([PlayerId]),
    CONSTRAINT [UQ_GamePlayers_GamePlayer] UNIQUE ([GameId], [PlayerId])
);
GO

-- Index for player history
CREATE NONCLUSTERED INDEX [IX_GamePlayers_PlayerId] 
ON [dbo].[GamePlayers] ([PlayerId])
INCLUDE ([GameId], [IsWinner], [FinalScore], [DartsThrown]);
GO
