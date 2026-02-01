-- Seed fake players for online testing
SET QUOTED_IDENTIFIER ON;
GO

USE DartsMobDB;
GO

-- Insert fake players if they don't exist
INSERT INTO Players (PlayerId, Nickname, Email, CreatedAt, UpdatedAt, IsActive)
SELECT NEWID(), name, LOWER(REPLACE(name, ' ', '')) + '@dartsmob.fake', GETUTCDATE(), GETUTCDATE(), 1
FROM (VALUES 
    ('DartMaster180'), ('BullseyeKing'), ('TripleTwenty'), ('ArrowAce'), ('SteelTipSteve'),
    ('FlightPath'), ('Checkout_Charlie'), ('DoubleTrouble'), ('CricketKing'), ('Shanghai_Sam'),
    ('NineDarter'), ('MadHouse'), ('TonEighty'), ('WireWizard'), ('BoardBoss'),
    ('FinishFirst'), ('OutshotOllie'), ('SegmentSlayer'), ('DartDemon'), ('BrassMonkey'),
    ('TungstenTom'), ('CorkChamp'), ('OcheMaster'), ('LegLegend'), ('SetStar')
) AS FakePlayers(name)
WHERE NOT EXISTS (SELECT 1 FROM Players WHERE Nickname = name);

-- Get the player IDs we just inserted
DECLARE @players TABLE (PlayerId UNIQUEIDENTIFIER, Nickname NVARCHAR(50));
INSERT INTO @players SELECT PlayerId, Nickname FROM Players WHERE Email LIKE '%@dartsmob.fake';

-- Seed registered boards with locations
INSERT INTO RegisteredBoards (Id, Name, OwnerId, Location, Latitude, Longitude, Timezone, IsPublic, CreatedAt, LastOnlineAt)
SELECT 
    NEWID(),
    p.Nickname + '''s Board',
    p.PlayerId,
    loc.City,
    loc.Lat + (RAND(CHECKSUM(NEWID())) - 0.5) * 2,
    loc.Lon + (RAND(CHECKSUM(NEWID())) - 0.5) * 2,
    loc.TZ,
    1,
    GETUTCDATE(),
    DATEADD(MINUTE, -ABS(CHECKSUM(NEWID())) % 60, GETUTCDATE())  -- Random last online in past hour
FROM @players p
CROSS APPLY (
    SELECT TOP 1 * FROM (VALUES
        ('New York', 40.7128, -74.0060, 'America/New_York'),
        ('Los Angeles', 34.0522, -118.2437, 'America/Los_Angeles'),
        ('Chicago', 41.8781, -87.6298, 'America/Chicago'),
        ('Houston', 29.7604, -95.3698, 'America/Chicago'),
        ('London', 51.5074, -0.1278, 'Europe/London'),
        ('Paris', 48.8566, 2.3522, 'Europe/Paris'),
        ('Berlin', 52.5200, 13.4050, 'Europe/Berlin'),
        ('Tokyo', 35.6762, 139.6503, 'Asia/Tokyo'),
        ('Sydney', -33.8688, 151.2093, 'Australia/Sydney'),
        ('Toronto', 43.6532, -79.3832, 'America/Toronto'),
        ('Mexico City', 19.4326, -99.1332, 'America/Mexico_City'),
        ('Mumbai', 19.0760, 72.8777, 'Asia/Kolkata'),
        ('Singapore', 1.3521, 103.8198, 'Asia/Singapore'),
        ('Dubai', 25.2048, 55.2708, 'Asia/Dubai'),
        ('Amsterdam', 52.3676, 4.9041, 'Europe/Amsterdam'),
        ('Seoul', 37.5665, 126.9780, 'Asia/Seoul'),
        ('Miami', 25.7617, -80.1918, 'America/New_York'),
        ('Las Vegas', 36.1699, -115.1398, 'America/Los_Angeles'),
        ('Denver', 39.7392, -104.9903, 'America/Denver'),
        ('Seattle', 47.6062, -122.3321, 'America/Los_Angeles'),
        ('Boston', 42.3601, -71.0589, 'America/New_York'),
        ('Dallas', 32.7767, -96.7970, 'America/Chicago'),
        ('Atlanta', 33.7490, -84.3880, 'America/New_York'),
        ('Phoenix', 33.4484, -112.0740, 'America/Phoenix'),
        ('Philadelphia', 39.9526, -75.1652, 'America/New_York')
    ) AS Locs(City, Lat, Lon, TZ)
    ORDER BY NEWID()
) loc
WHERE NOT EXISTS (SELECT 1 FROM RegisteredBoards WHERE OwnerId = p.PlayerId);

-- Seed player ratings
INSERT INTO PlayerRatings (Id, PlayerId, GameMode, Rating, GamesPlayed, Wins, Losses, AverageScore, CheckoutPercentage, Highest180s, UpdatedAt)
SELECT 
    NEWID(),
    p.PlayerId,
    'Game501',
    1000 + ABS(CHECKSUM(NEWID())) % 600,  -- Rating 1000-1600
    10 + ABS(CHECKSUM(NEWID())) % 200,    -- 10-210 games
    5 + ABS(CHECKSUM(NEWID())) % 100,     -- Wins
    5 + ABS(CHECKSUM(NEWID())) % 100,     -- Losses
    30.0 + (ABS(CHECKSUM(NEWID())) % 400) / 10.0,  -- Avg 30-70
    10.0 + (ABS(CHECKSUM(NEWID())) % 500) / 10.0,  -- Checkout 10-60%
    ABS(CHECKSUM(NEWID())) % 50,          -- 180s
    GETUTCDATE()
FROM @players p
WHERE NOT EXISTS (SELECT 1 FROM PlayerRatings WHERE PlayerId = p.PlayerId AND GameMode = 'Game501');

-- Show what we seeded
SELECT 'Players' AS [Table], COUNT(*) AS [Count] FROM Players WHERE Email LIKE '%@dartsmob.fake'
UNION ALL
SELECT 'RegisteredBoards', COUNT(*) FROM RegisteredBoards rb JOIN Players p ON rb.OwnerId = p.PlayerId WHERE p.Email LIKE '%@dartsmob.fake'
UNION ALL
SELECT 'PlayerRatings', COUNT(*) FROM PlayerRatings pr JOIN Players p ON pr.PlayerId = p.PlayerId WHERE p.Email LIKE '%@dartsmob.fake';

PRINT 'Fake players seeded successfully!';
