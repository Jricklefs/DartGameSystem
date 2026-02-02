-- Add CalibrationData column to Boards table  
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Boards' AND COLUMN_NAME = 'CalibrationData')  
BEGIN  
    ALTER TABLE Boards ADD CalibrationData NVARCHAR(MAX) NULL;  
    PRINT 'Added CalibrationData column to Boards table';  
END  
ELSE  
    PRINT 'CalibrationData column already exists'; 
