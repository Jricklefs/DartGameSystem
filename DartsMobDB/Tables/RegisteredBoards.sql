/*
================================================================================
    REGISTEREDBOARDS - Remote boards for online play
================================================================================
    Notes:
    - Boards registered for online/multi-location play
    - Includes location for 3D globe visualization
    - OwnerId links to player who registered the board
*/

CREATE TABLE [dbo].[RegisteredBoards]
(
    [Id]            UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [Name]          NVARCHAR(100)       NOT NULL,
    [OwnerId]       UNIQUEIDENTIFIER    NOT NULL,   -- Player who owns/registered
    [Location]      NVARCHAR(100)       NULL,       -- City/venue name
    [Latitude]      FLOAT               NULL,
    [Longitude]     FLOAT               NULL,
    [Timezone]      NVARCHAR(50)        NULL,
    [IsPublic]      BIT                 NOT NULL    DEFAULT 1,
    [CreatedAt]     DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),
    [LastOnlineAt]  DATETIME2           NULL,

    CONSTRAINT [PK_RegisteredBoards] PRIMARY KEY CLUSTERED ([Id])
);
GO
