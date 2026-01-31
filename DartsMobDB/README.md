# DartsMobDB - Database Project

SQL Server Database Project for DartsMob player and game history.

## Schema Overview

### Tables

| Table | Purpose |
|-------|---------|
| `Players` | Player profiles (nickname, email, avatar) |
| `Boards` | Physical dartboard locations |
| `Games` | Game sessions (mode, winner, duration) |
| `GamePlayers` | Players in each game (scores, darts thrown) |
| `Throws` | Individual dart throws (segment, score, position) |

### Views

| View | Purpose |
|------|---------|
| `vw_PlayerStats` | Lifetime statistics per player |
| `vw_GameHistory` | Game history with player details |

## Setup

### 1. Create Database

Run `Scripts/CreateDatabase.sql` on your SQL Server:
- Creates `DartsMobDB` database
- Creates `DartsMobApp` login/user with limited permissions

```sql
sqlcmd -S localhost -i Scripts/CreateDatabase.sql
```

### 2. Deploy Schema

**Option A: Visual Studio**
1. Open `DartsMobDB.sqlproj` in Visual Studio
2. Right-click project → Publish
3. Select target database connection

**Option B: Build and Deploy via CLI**
```bash
dotnet build
sqlpackage /Action:Publish /SourceFile:bin/Debug/DartsMobDB.dacpac /TargetConnectionString:"Server=localhost;Database=DartsMobDB;Trusted_Connection=True;"
```

### 3. Seed Data

Run `Scripts/SeedData.sql` to add default board:
```sql
sqlcmd -S localhost -d DartsMobDB -i Scripts/SeedData.sql
```

## Connection String

For DartGameAPI `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DartsMobDB": "Server=localhost;Database=DartsMobDB;User Id=DartsMobApp;Password=Stewart14s!2;TrustServerCertificate=True;"
  }
}
```

## User Stories Implemented

- [x] Player creates profile (name, optional avatar)
- [x] Player views their game history
- [x] Player views lifetime stats
- [x] Host starts game, selects existing players
- [x] System records each throw during game
- [x] System records game outcome (winner, final scores)

## Future

- [ ] Leaderboards
- [ ] Achievements
- [ ] Tournaments/leagues
- [ ] Multiple venues
