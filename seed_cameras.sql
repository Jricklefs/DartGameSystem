-- Seed default board and cameras

-- Create default board if it doesn't exist
IF NOT EXISTS (SELECT 1 FROM Boards WHERE BoardId = 'default')
BEGIN
    INSERT INTO Boards (BoardId, Name, Location, CameraCount, IsCalibrated, IsActive, CreatedAt)
    VALUES ('default', 'Default Board', 'Garage', 3, 0, 1, GETUTCDATE())
    PRINT 'Created default board'
END
ELSE
BEGIN
    PRINT 'Default board already exists'
END
GO

-- Seed cameras for default board with calibration from existing Calibrations table
INSERT INTO Cameras (CameraId, BoardId, DeviceIndex, DisplayName, IsCalibrated, CalibrationQuality, LastCalibration, CreatedAt, IsActive)
SELECT 
    c.CameraId,
    'default',
    CAST(REPLACE(c.CameraId, 'cam', '') AS INT),
    'Camera ' + REPLACE(c.CameraId, 'cam', ''),
    1,  -- Already calibrated
    c.Quality,
    c.UpdatedAt,
    GETUTCDATE(),
    1
FROM Calibrations c
WHERE NOT EXISTS (SELECT 1 FROM Cameras WHERE BoardId = 'default' AND CameraId = c.CameraId)

PRINT 'Cameras seeded'
GO

-- Update board calibration status
UPDATE Boards SET IsCalibrated = 1, CameraCount = 3, LastCalibration = GETUTCDATE() WHERE BoardId = 'default'
PRINT 'Board updated'
GO
