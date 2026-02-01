-- ============================================================================
-- DartsMob Online Play Tables Migration
-- Run this on DartsMobDB
-- ============================================================================

USE DartsMobDB;
GO

-- RegisteredBoards - Boards registered for online play
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RegisteredBoards')
BEGIN
    CREATE TABLE RegisteredBoards (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        Name NVARCHAR(100) NOT NULL,
        OwnerId UNIQUEIDENTIFIER NOT NULL,
        Location NVARCHAR(100) NULL,
        Latitude FLOAT NULL,
        Longitude FLOAT NULL,
        Timezone NVARCHAR(50) NULL,
        IsPublic BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        LastOnlineAt DATETIME2 NULL,
        CONSTRAINT FK_RegisteredBoards_Players FOREIGN KEY (OwnerId) 
            REFERENCES Players(PlayerId)
    );
    
    CREATE INDEX IX_RegisteredBoards_OwnerId ON RegisteredBoards(OwnerId);
    CREATE INDEX IX_RegisteredBoards_Location ON RegisteredBoards(Latitude, Longitude) WHERE Latitude IS NOT NULL;
    
    PRINT 'Created RegisteredBoards table';
END
GO

-- Friendships - Player friendships
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Friendships')
BEGIN
    CREATE TABLE Friendships (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        RequesterId UNIQUEIDENTIFIER NOT NULL,
        AddresseeId UNIQUEIDENTIFIER NOT NULL,
        Status INT NOT NULL DEFAULT 0,  -- 0=Pending, 1=Accepted, 2=Declined, 3=Blocked
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        AcceptedAt DATETIME2 NULL,
        CONSTRAINT FK_Friendships_Requester FOREIGN KEY (RequesterId) 
            REFERENCES Players(PlayerId),
        CONSTRAINT FK_Friendships_Addressee FOREIGN KEY (AddresseeId) 
            REFERENCES Players(PlayerId),
        CONSTRAINT UQ_Friendships_Pair UNIQUE (RequesterId, AddresseeId)
    );
    
    CREATE INDEX IX_Friendships_Requester ON Friendships(RequesterId);
    CREATE INDEX IX_Friendships_Addressee ON Friendships(AddresseeId);
    CREATE INDEX IX_Friendships_Status ON Friendships(Status);
    
    PRINT 'Created Friendships table';
END
GO

-- PlayerRatings - Skill ratings per game mode
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PlayerRatings')
BEGIN
    CREATE TABLE PlayerRatings (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        PlayerId UNIQUEIDENTIFIER NOT NULL,
        GameMode NVARCHAR(50) NOT NULL,
        Rating INT NOT NULL DEFAULT 1200,
        GamesPlayed INT NOT NULL DEFAULT 0,
        Wins INT NOT NULL DEFAULT 0,
        Losses INT NOT NULL DEFAULT 0,
        AverageScore FLOAT NOT NULL DEFAULT 0,
        CheckoutPercentage FLOAT NOT NULL DEFAULT 0,
        Highest180s INT NOT NULL DEFAULT 0,
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_PlayerRatings_Players FOREIGN KEY (PlayerId) 
            REFERENCES Players(PlayerId),
        CONSTRAINT UQ_PlayerRatings_Mode UNIQUE (PlayerId, GameMode)
    );
    
    CREATE INDEX IX_PlayerRatings_Rating ON PlayerRatings(GameMode, Rating DESC);
    
    PRINT 'Created PlayerRatings table';
END
GO

-- Availability - Player availability schedule
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Availability')
BEGIN
    CREATE TABLE Availability (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        PlayerId UNIQUEIDENTIFIER NOT NULL,
        DayOfWeek INT NULL,  -- 0=Sunday through 6=Saturday, NULL for specific date
        SpecificDate DATE NULL,  -- For one-time availability
        StartTime TIME NOT NULL,
        EndTime TIME NOT NULL,
        Timezone NVARCHAR(50) NULL,
        IsRecurring BIT NOT NULL DEFAULT 1,
        CONSTRAINT FK_Availability_Players FOREIGN KEY (PlayerId) 
            REFERENCES Players(PlayerId)
    );
    
    CREATE INDEX IX_Availability_Player ON Availability(PlayerId);
    CREATE INDEX IX_Availability_Day ON Availability(DayOfWeek, StartTime, EndTime);
    
    PRINT 'Created Availability table';
END
GO

PRINT 'Online Play migration complete!';
