/*
================================================================================
    AVAILABILITY - Player availability for matchmaking
================================================================================
    Notes:
    - Players can set recurring or specific date availability
    - Used for scheduling matches and finding opponents
*/

CREATE TABLE [dbo].[Availability]
(
    [Id]            UNIQUEIDENTIFIER    NOT NULL    DEFAULT NEWID(),
    [PlayerId]      UNIQUEIDENTIFIER    NOT NULL,
    [DayOfWeek]     INT                 NULL,       -- 0=Sunday, 6=Saturday (for recurring)
    [SpecificDate]  DATE                NULL,       -- For one-time availability
    [StartTime]     TIME                NOT NULL,
    [EndTime]       TIME                NOT NULL,
    [Timezone]      NVARCHAR(50)        NULL,
    [IsRecurring]   BIT                 NOT NULL    DEFAULT 0,

    CONSTRAINT [PK_Availability] PRIMARY KEY CLUSTERED ([Id])
);
GO
