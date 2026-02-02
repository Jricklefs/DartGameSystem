/*
================================================================================
    PLAYERRATINGS - Elo ratings per game mode
================================================================================
    Notes:
    - Each player has a rating per game mode (X01, Cricket, etc.)
    - Tracks stats for leaderboards
*/

CREATE TABLE [dbo].[PlayerRatings]
(
    [Id]                    UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [PlayerId]              UNIQUEIDENTIFIER    NOT NULL,
    [GameMode]              NVARCHAR(50)        NOT NULL,   -- 'X01', 'Cricket', etc.
    [Rating]                INT                 NOT NULL    DEFAULT 1500,
    [GamesPlayed]           INT                 NOT NULL    DEFAULT 0,
    [Wins]                  INT                 NOT NULL    DEFAULT 0,
    [Losses]                INT                 NOT NULL    DEFAULT 0,
    [AverageScore]          FLOAT               NOT NULL    DEFAULT 0,
    [CheckoutPercentage]    FLOAT               NOT NULL    DEFAULT 0,
    [Highest180s]           INT                 NOT NULL    DEFAULT 0,
    [UpdatedAt]             DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_PlayerRatings] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_PlayerRatings_PlayerMode] UNIQUE ([PlayerId], [GameMode])
);
GO
