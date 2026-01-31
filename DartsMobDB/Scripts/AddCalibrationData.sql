/*
================================================================================
    Add CalibrationData column to Boards table
================================================================================
    Stores the full calibration JSON from DartDetect API.
    This allows backup/restore of calibration across installs.
*/

ALTER TABLE [dbo].[Boards]
ADD [CalibrationData] NVARCHAR(MAX) NULL;
GO

-- Add comment
EXEC sp_addextendedproperty 
    @name = N'MS_Description', 
    @value = N'JSON blob containing camera calibration data (homography matrices, reference points)', 
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE',  @level1name = N'Boards',
    @level2type = N'COLUMN', @level2name = N'CalibrationData';
GO
