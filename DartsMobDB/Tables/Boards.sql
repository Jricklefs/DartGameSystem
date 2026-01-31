/*
================================================================================
    BOARDS - Physical dartboard locations
================================================================================
    Notes:
    - Tracks physical boards (could be at different venues)
    - Links to calibration data in DartDetect API
    - Future: venue/location info
*/

CREATE TABLE [dbo].[Boards]
(
    [BoardId]       NVARCHAR(50)        NOT NULL,       -- Matches DartDetect board_id
    [Name]          NVARCHAR(100)       NOT NULL,
    [Location]      NVARCHAR(255)       NULL,           -- Optional venue/room
    [CameraCount]   INT                 NOT NULL    DEFAULT 3,
    [IsCalibrated]  BIT                 NOT NULL    DEFAULT 0,
    [LastCalibration] DATETIME2         NULL,
    [CreatedAt]     DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),
    [IsActive]      BIT                 NOT NULL    DEFAULT 1,
    [CalibrationData] NVARCHAR(MAX)     NULL,           -- JSON blob from DartDetect

    CONSTRAINT [PK_Boards] PRIMARY KEY CLUSTERED ([BoardId])
);
GO
