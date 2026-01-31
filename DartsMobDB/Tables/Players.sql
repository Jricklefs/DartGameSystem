/*
================================================================================
    PLAYERS - Core player profiles
================================================================================
    User Story: As a player, I can create a profile with my name
    
    Notes:
    - PlayerId is GUID for easy sync across systems
    - Nickname is display name (can change)
    - CreatedAt tracks when player first registered
    - IsActive allows soft-delete without losing history
*/

CREATE TABLE [dbo].[Players]
(
    [PlayerId]      UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [Nickname]      NVARCHAR(50)        NOT NULL,
    [Email]         NVARCHAR(255)       NULL,           -- Optional, for future auth
    [AvatarUrl]     NVARCHAR(500)       NULL,           -- Optional profile pic
    [CreatedAt]     DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),
    [UpdatedAt]     DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),
    [IsActive]      BIT                 NOT NULL    DEFAULT 1,

    CONSTRAINT [PK_Players] PRIMARY KEY CLUSTERED ([PlayerId]),
    CONSTRAINT [UQ_Players_Nickname] UNIQUE ([Nickname])
);
GO

-- Index for lookups by nickname
CREATE NONCLUSTERED INDEX [IX_Players_Nickname] 
ON [dbo].[Players] ([Nickname]) 
WHERE [IsActive] = 1;
GO
