/*
================================================================================
    CAMERAS - Individual cameras per board
================================================================================
    Notes:
    - Each board can have multiple cameras (typically 3)
    - Tracks calibration status per camera
    - Links to parent Board
*/

CREATE TABLE [dbo].[Cameras]
(
    [Id]                    INT             IDENTITY(1,1)   NOT NULL,
    [CameraId]              NVARCHAR(50)    NOT NULL,       -- e.g. 'cam0', 'cam1', 'cam2'
    [BoardId]               NVARCHAR(50)    NOT NULL,       -- FK to Boards
    [DeviceIndex]           INT             NOT NULL,       -- USB device index
    [DisplayName]           NVARCHAR(100)   NULL,           -- Friendly name
    [IsCalibrated]          BIT             NOT NULL        DEFAULT 0,
    [CalibrationQuality]    DECIMAL(5,4)    NULL,           -- 0.0000 to 1.0000
    [LastCalibration]       DATETIME2       NULL,
    [CreatedAt]             DATETIME2       NOT NULL        DEFAULT GETUTCDATE(),
    [IsActive]              BIT             NOT NULL        DEFAULT 1,

    CONSTRAINT [PK_Cameras] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Cameras_Boards] FOREIGN KEY ([BoardId]) REFERENCES [dbo].[Boards]([BoardId]),
    CONSTRAINT [UQ_Cameras_BoardCamera] UNIQUE ([BoardId], [CameraId])
);
GO
