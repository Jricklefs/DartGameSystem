/*
================================================================================
    CALIBRATIONS - Camera calibration data
================================================================================
    Notes:
    - Stores calibration results per camera
    - Includes image paths and quality metrics
*/

CREATE TABLE [dbo].[Calibrations]
(
    [Id]                    INT             IDENTITY(1,1)   NOT NULL,
    [CameraId]              NVARCHAR(50)    NOT NULL,
    [CalibrationImagePath]  NVARCHAR(500)   NULL,
    [OverlayImagePath]      NVARCHAR(500)   NULL,
    [Quality]               FLOAT           NOT NULL        DEFAULT 0,
    [TwentyAngle]           FLOAT           NULL,           -- Angle to segment 20
    [CalibrationData]       NVARCHAR(MAX)   NULL,           -- JSON blob
    [CreatedAt]             DATETIME2       NULL            DEFAULT GETUTCDATE(),
    [UpdatedAt]             DATETIME2       NULL,

    CONSTRAINT [PK_Calibrations] PRIMARY KEY CLUSTERED ([Id])
);
GO
