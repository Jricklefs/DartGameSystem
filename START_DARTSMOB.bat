@echo off
title DartsMob Launcher
color 0A

echo.
echo  ========================================
echo    🎯 DARTSMOB - Starting System...
echo  ========================================
echo.

:: Kill any existing instances first
echo  [0/4] Stopping any existing instances...
taskkill /IM "DartGameAPI.exe" /F >nul 2>&1
taskkill /IM "dotnet.exe" /F >nul 2>&1
taskkill /IM "python.exe" /F >nul 2>&1
timeout /t 2 /nobreak > nul

:: Start DartDetect API (Python/FastAPI on port 8000)
echo  [1/4] Starting DartDetect AI on port 8000...
cd /d "C:\Users\clawd\DartDetectionAI"
start "DartDetect API" cmd /c "C:\Users\clawd\Python312\python.exe -m uvicorn app.main:app --host 0.0.0.0 --port 8000"

timeout /t 3 /nobreak > nul

:: Start DartSensor API (Python/Flask on port 8001)
echo  [2/4] Starting DartSensor API on port 8001...
cd /d "C:\Users\clawd\DartSensor"
start "DartSensor API" cmd /c "C:\Users\clawd\DartDetectionAI\.venv\Scripts\python.exe src/DartSensorAPI.py"

timeout /t 2 /nobreak > nul

:: Build and start DartGame API (.NET on port 5000)
echo  [3/4] Building DartGame API...
cd /d "C:\Users\clawd\DartGameSystem\DartGameAPI"
dotnet build --configuration Release -v q

echo  [4/4] Starting DartGame API on port 5000...
start "DartsMob API" dotnet run --configuration Release --no-build --urls "http://0.0.0.0:5000"

timeout /t 4 /nobreak > nul

echo  [+] Opening browser in kiosk mode...
start http://localhost:5000?kiosk=1

echo.
echo  ========================================
echo    ✓ DartsMob System Running!
echo    
echo    Game UI:      http://localhost:5000
echo    Kiosk Mode:   http://localhost:5000?kiosk=1
echo    Game API:     http://localhost:5000/swagger
echo    Detect API:   http://localhost:8000/docs
echo    Sensor API:   http://localhost:8001
echo    Architecture: http://localhost:5000/architecture.html
echo    
echo    Press any key to stop ALL services...
echo  ========================================
echo.

pause > nul

echo.
echo  Shutting down all DartsMob services...

:: Kill Python processes (DartDetect + DartSensor)
echo  - Stopping Python APIs...
taskkill /IM "python.exe" /F >nul 2>&1

:: Kill .NET processes (DartGame)
echo  - Stopping DartGame API...
taskkill /IM "DartGameAPI.exe" /F >nul 2>&1
taskkill /IM "dotnet.exe" /F >nul 2>&1

:: Kill any remaining CMD windows we started
echo  - Cleaning up windows...
taskkill /FI "WINDOWTITLE eq DartDetect API*" /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq DartSensor API*" /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq DartsMob API*" /F >nul 2>&1

echo.
echo  ✓ All services stopped.
timeout /t 2 /nobreak > nul
