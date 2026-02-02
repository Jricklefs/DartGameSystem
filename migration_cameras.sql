-- Migration: Add Cameras table for tracking individual cameras per board
-- Run on DartsMobDB

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Cameras')
BEGIN
    CREATE TABLE Cameras (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        CameraId NVARCHAR(50) NOT NULL,           -- e.g. "cam0", "cam1"
        BoardId NVARCHAR(50) NOT NULL,            -- FK to Boards
        DeviceIndex INT NOT NULL,                 -- USB device index
        DisplayName NVARCHAR(100) NULL,           -- User-friendly name
        IsCalibrated BIT NOT NULL DEFAULT 0,
        CalibrationQuality DECIMAL(5,4) NULL,     -- 0.0000 - 1.0000
        LastCalibration DATETIME2 NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        IsActive BIT NOT NULL DEFAULT 1,
        
        CONSTRAINT FK_Cameras_Boards FOREIGN KEY (BoardId) REFERENCES Boards(BoardId),
        CONSTRAINT UQ_Cameras_BoardCamera UNIQUE (BoardId, CameraId)
    );

    CREATE INDEX IX_Cameras_BoardId ON Cameras(BoardId);
    
    PRINT 'Created Cameras table';
END
ELSE
BEGIN
    PRINT 'Cameras table already exists';
END
GO

-- Seed cameras for default board if it exists
IF EXISTS (SELECT 1 FROM Boards WHERE BoardId = 'default')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM Cameras WHERE BoardId = 'default')
    BEGIN
        INSERT INTO Cameras (CameraId, BoardId, DeviceIndex, DisplayName, IsCalibrated, CreatedAt, IsActive)
        VALUES 
            ('cam0', 'default', 0, 'Camera 0', 0, GETUTCDATE(), 1),
            ('cam1', 'default', 1, 'Camera 1', 0, GETUTCDATE(), 1),
            ('cam2', 'default', 2, 'Camera 2', 0, GETUTCDATE(), 1);
        
        UPDATE Boards SET CameraCount = 3 WHERE BoardId = 'default';
        
        PRINT 'Seeded 3 cameras for default board';
    END
END
GO
