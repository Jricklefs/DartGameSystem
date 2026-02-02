/*
================================================================================
    FRIENDSHIPS - Player friend relationships
================================================================================
    Notes:
    - Tracks friend requests and accepted friendships
    - Status: 0=Pending, 1=Accepted, 2=Declined, 3=Blocked
*/

CREATE TABLE [dbo].[Friendships]
(
    [Id]            UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [RequesterId]   UNIQUEIDENTIFIER    NOT NULL,   -- Player who sent request
    [AddresseeId]   UNIQUEIDENTIFIER    NOT NULL,   -- Player who received request
    [Status]        INT                 NOT NULL    DEFAULT 0,
    [CreatedAt]     DATETIME2           NOT NULL    DEFAULT GETUTCDATE(),
    [AcceptedAt]    DATETIME2           NULL,

    CONSTRAINT [PK_Friendships] PRIMARY KEY CLUSTERED ([Id])
);
GO
