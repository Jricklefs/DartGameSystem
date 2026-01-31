/*
================================================================================
    CreateDatabase.sql - Initial database creation script
================================================================================
    Run this on SQL Server to create the DartsMobDB database.
    
    After running this, deploy the .sqlproj to create tables/views.
*/

USE [master];
GO

-- Create database if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'DartsMobDB')
BEGIN
    CREATE DATABASE [DartsMobDB]
    COLLATE SQL_Latin1_General_CP1_CI_AS;
    PRINT 'Database DartsMobDB created.';
END
ELSE
BEGIN
    PRINT 'Database DartsMobDB already exists.';
END
GO

-- Create login for application
IF NOT EXISTS (SELECT name FROM sys.server_principals WHERE name = N'DartsMobApp')
BEGIN
    CREATE LOGIN [DartsMobApp] WITH PASSWORD = N'Stewart14s!2', 
        DEFAULT_DATABASE = [DartsMobDB],
        CHECK_EXPIRATION = OFF,
        CHECK_POLICY = OFF;
    PRINT 'Login DartsMobApp created.';
END
ELSE
BEGIN
    PRINT 'Login DartsMobApp already exists.';
END
GO

USE [DartsMobDB];
GO

-- Create user for login
IF NOT EXISTS (SELECT name FROM sys.database_principals WHERE name = N'DartsMobApp')
BEGIN
    CREATE USER [DartsMobApp] FOR LOGIN [DartsMobApp];
    ALTER ROLE [db_datareader] ADD MEMBER [DartsMobApp];
    ALTER ROLE [db_datawriter] ADD MEMBER [DartsMobApp];
    GRANT EXECUTE TO [DartsMobApp];
    PRINT 'User DartsMobApp created with read/write/execute permissions.';
END
GO

PRINT 'Database setup complete. Now deploy the DartsMobDB.sqlproj to create tables.';
GO
