/*
================================================================================
    DARTLOCATIONS - Individual dart throw locations for heatmaps
================================================================================
    Notes:
    - Records X/Y mm position of every dart thrown
    - Used for heatmap visualization and accuracy analysis
    - Links to game and player
*/

CREATE TABLE [dbo].[DartLocations]
(
    [Id]            INT             IDENTITY(1,1)   NOT NULL,
    [GameId]        NVARCHAR(50)    NULL,
    [PlayerId]      NVARCHAR(50)    NULL,
    [TurnNumber]    INT             NOT NULL,
    [DartIndex]     INT             NOT NULL,       -- 0, 1, or 2 within turn
    [XMm]           DECIMAL(10,2)   NOT NULL,
    [YMm]           DECIMAL(10,2)   NOT NULL,
    [Segment]       INT             NOT NULL,
    [Multiplier]    INT             NOT NULL,
    [Score]         INT             NOT NULL,
    [Confidence]    DECIMAL(5,4)    NOT NULL,
    [CameraId]      NVARCHAR(50)    NULL,
    [DetectedAt]    DATETIME2       NOT NULL        DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_DartLocations] PRIMARY KEY CLUSTERED ([Id])
);
GO
