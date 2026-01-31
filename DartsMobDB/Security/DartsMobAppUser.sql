/*
================================================================================
    DartsMobAppUser - Application database user
================================================================================
    Creates a SQL login and user for the DartGameSystem API to connect.
    
    IMPORTANT: 
    - Change the password before deploying to production!
    - This user has limited permissions (no schema changes)
    
    Run this AFTER creating the database.
*/

-- Create login at server level (run on master)
-- USE [master];
-- GO
-- CREATE LOGIN [DartsMobApp] WITH PASSWORD = 'DartsMob2026!';
-- GO

-- Create contained user in DartsMobDB database
--?