================================================================================
                            DARTSMOB - QUICK START
================================================================================

LOCATION: C:\Users\clawd\DartGameSystem


OPTION 1: DOUBLE-CLICK TO RUN
-----------------------------
Just double-click: RUN_DARTSMOB.bat


OPTION 2: COMMAND LINE
----------------------
Open PowerShell or Command Prompt:

    cd C:\Users\clawd\DartGameSystem\DartGameAPI
    dotnet run --urls http://0.0.0.0:5000


ACCESS THE UI
-------------
Open a browser and go to:

    http://localhost:5000          (from this PC)
    http://192.168.0.158:5000      (from other devices on network)


API DOCS (SWAGGER)
------------------
    http://localhost:5000/swagger


TEST WITHOUT CAMERAS
--------------------
1. Start a game in the UI
2. Use the API to manually submit throws:

    POST http://localhost:5000/api/games/{gameId}/manual
    Content-Type: application/json
    
    {"segment": 20, "multiplier": 3}    // Triple 20 = 60 points
    {"segment": 20, "multiplier": 1}    // Single 20 = 20 points
    {"segment": 25, "multiplier": 2}    // Bullseye = 50 points
    {"segment": 25, "multiplier": 1}    // Outer bull = 25 points


UPDATE TO LATEST
----------------
    cd C:\Users\clawd\DartGameSystem
    git pull


PORTS
-----
DartsMob UI:      5000
DartDetect API:   8000 (separate service)


================================================================================
