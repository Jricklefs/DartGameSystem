/*
================================================================================
    SeedData.sql - Initial seed data
================================================================================
    Run this after deploying the schema to populate default data.
*/

USE [DartsMobDB];
GO

-- Default board
IF NOT EXISTS (SELECT 1 FROM [dbo].[Boards] WHERE BoardId = 'default')
BEGIN
    INSERT INTO [dbo].[Boards] (BoardId, Name, Location, CameraCount, IsCalibrated)
    VALUES ('default', 'Main Board', 'Game Room', 3, 0);
    PRINT 'Default board created.';
END
GO

-- Sample players (optional, comment out if not wanted)
/*
IF NOT EXISTS (SELECT 1 FROM [dbo].[Players] WHERE Nickname = 'Vinnie')
BEGIN
    INSERT INTO [dbo].[Players] (Nickname) VALUES ('Vinnie');
    INSERT INTO [dbo].[Players] (Nickname) VALUES ('Tommy');
    INSERT INTO [dbo].[Players] (Nickname) VALUES ('Sal');
    PRINT 'Sample players created.';
END
GO
*/

PRINT 'Seed data complete.';
GO
