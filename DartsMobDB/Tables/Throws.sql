/*
================================================================================
    THROWS - Individual dart throws
================================================================================
    User Story: System records each throw during game
    
    Notes:
    - Every dart thrown gets a record
    - TurnNumber groups 3 darts together
    - DartIndex is 0, 1, 2 within turn
    - Segment: 1-20, or 25 for bull
    - Multiplier: 1=single, 2=double, 3=triple
    - Score = Segment * Multiplier (except bull: 25 or 50)
    - XMm/YMm: Position from board center (for heatmaps)
*/

CREATE TABLE [dbo].[Throws]
(
    [ThrowId]       UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [GamePlayerId]  UNIQUEIDENTIFIER    NOT NULL,
    [TurnNumber]    INT                 NOT NULL,       -- 1-based turn in game
    [DartIndex]     INT                 NOT NULL,       -- 0, 1, 2 within turn
    [Segment]       INT                 NOT NULL,       -- 1-20, 25 for bull, 0 for miss
    [Multiplier]    INT                 NOT NULL,       -- 1, 2, 3
    [Score]         INT                 NOT NULL,       -- Calculated points
    [XMm]           DECIMAL(8,2)        NULL,           -- X position from center
    [YMm]           DECIMAL(8,2)        NULL,           -- Y position from center
    [Confidence]    DECIMAL(5,4)        NULL,           -- Detection confidence 0-1
    [IsBust]        BIT                 NOT NULL    DEFAULT 0,  -- Was this a bust throw
    [ThrownAt]      DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Throws] PRIMARY KEY CLUSTERED ([ThrowId]),
    CONSTRAINT [FK_Throws_GamePlayers] FOREIGN KEY ([GamePlayerId]) REFERENCES [dbo].[GamePlayers]([GamePlayerId]),
    CONSTRAINT [CK_Throws_DartIndex] CHECK ([DartIndex] >= 0 AND [DartIndex] <= 2),
    CONSTRAINT [CK_Throws_Segment] CHECK ([Segment] >= 0 AND [Segment] <= 25),
    CONSTRAINT [CK_Throws_Multiplier] CHECK ([Multiplier] >= 1 AND [Multiplier] <= 3)
);
GO

-- Index for getting all throws in a game efficiently
CREATE NONCLUSTERED INDEX [IX_Throws_GamePlayerId_TurnNumber] 
ON [dbo].[Throws] ([GamePlayerId], [TurnNumber], [DartIndex]);
GO

-- Index for analytics (segment popularity, etc.)
CREATE NONCLUSTERED INDEX [IX_Throws_Segment_Multiplier] 
ON [dbo].[Throws] ([Segment], [Multiplier])
INCLUDE ([Score]);
GO
