/*
================================================================================
    GAMES - Game sessions
================================================================================
    User Story: As a host, I can start a game and pick players
    User Story: System records game outcome (winner, final scores)
    
    Notes:
    - One record per game session
    - GameMode: Practice=0, Game501=1, Game301=2, Cricket=3
    - GameState: InProgress=0, Finished=1, Cancelled=2
*/

CREATE TABLE [dbo].[Games]
(
    [GameId]        UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [BoardId]       NVARCHAR(50)        NOT NULL,
    [GameMode]      INT                 NOT NULL    DEFAULT 0,  -- Practice
    [GameState]     INT                 NOT NULL    DEFAULT 0,  -- InProgress
    [StartedAt]     DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),
    [EndedAt]       DATETIME2           NULL,
    [WinnerPlayerId] UNIQUEIDENTIFIER   NULL,
    [TotalDarts]    INT                 NOT NULL    DEFAULT 0,
    [DurationSeconds] INT               NULL,

    CONSTRAINT [PK_Games] PRIMARY KEY CLUSTERED ([GameId]),
    CONSTRAINT [FK_Games_Boards] FOREIGN KEY ([BoardId]) REFERENCES [dbo].[Boards]([BoardId]),
    CONSTRAINT [FK_Games_WinnerPlayer] FOREIGN KEY ([WinnerPlayerId]) REFERENCES [dbo].[Players]([PlayerId])
);
GO

-- Index for recent games lookup
CREATE NONCLUSTERED INDEX [IX_Games_StartedAt] 
ON [dbo].[Games] ([StartedAt] DESC);
GO

-- Index for player game history (via winner)
CREATE NONCLUSTERED INDEX [IX_Games_WinnerPlayerId] 
ON [dbo].[Games] ([WinnerPlayerId]) 
WHERE [WinnerPlayerId] IS NOT NULL;
GO
