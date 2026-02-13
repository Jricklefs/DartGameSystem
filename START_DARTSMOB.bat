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

:: Start DartDetect API (Python/FastAPI on port 8000) - BOTTOM RIGHT
echo  [1/4] Starting DartDetect AI on port 8000...
cd /d "C:\Users\clawd\DartDetectionAI"
start "DartDetect API" cmd /c "mode con: cols=80 lines=25 & C:\Users\clawd\Python312\python.exe -m uvicorn app.main:app --host 0.0.0.0 --port 8000"
timeout /t 1 /nobreak > nul
powershell -command "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class Win32 { [DllImport(\"user32.dll\")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags); [DllImport(\"user32.dll\")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName); }'; $h = [Win32]::FindWindow($null, 'DartDetect API'); if($h -ne [IntPtr]::Zero) { [Win32]::SetWindowPos($h, [IntPtr]::Zero, 960, 840, 960, 420, 0) }"

timeout /t 2 /nobreak > nul

:: Build and start DartGame API (.NET on port 5000)
echo  [2/4] Building DartGame API...
cd /d "C:\Users\clawd\DartGameSystem\DartGameAPI"
dotnet build --configuration Release -v q

echo  [3/4] Starting DartGame API on port 5000...
start "DartsMob API" cmd /c "mode con: cols=80 lines=25 & dotnet run --configuration Release --no-build --urls http://0.0.0.0:5000"

timeout /t 4 /nobreak > nul

:: Wait for DartGame API to be ready before starting sensor
echo  [+] Waiting for DartGame API to be ready...
:wait_api
timeout /t 1 /nobreak > nul
curl -s http://localhost:5000/health >nul 2>&1
if errorlevel 1 goto wait_api
echo  [+] DartGame API is ready!

:: Start DartSensor API (Python on port 8001) - TOP RIGHT
echo  [3/4] Starting DartSensor API on port 8001...
cd /d "C:\Users\clawd\DartSensor"
start "DartSensor API" cmd /c "mode con: cols=80 lines=25 & C:\Users\clawd\Python312\python.exe src/DartSensorAPI.py"
timeout /t 1 /nobreak > nul
powershell -command "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class Win32 { [DllImport(\"user32.dll\")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags); [DllImport(\"user32.dll\")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName); }'; $h = [Win32]::FindWindow($null, 'DartSensor API'); if($h -ne [IntPtr]::Zero) { [Win32]::SetWindowPos($h, [IntPtr]::Zero, 960, 0, 960, 420, 0) }"

timeout /t 1 /nobreak > nul

:: Open browser on LEFT half (0,0 to 960,1680)
echo  [4/4] Opening browser in kiosk mode (left half)...
start "" "C:\Program Files\Google\Chrome\Application\chrome.exe" --new-window --window-position=0,0 --window-size=960,1680 --app="http://localhost:5000?kiosk=1"

echo.
echo  ========================================
echo    ✓ DartsMob System Running!
echo    
echo    Layout:
echo      LEFT:         Game UI (browser)
echo      TOP-RIGHT:    DartSensor API
echo      BOTTOM-RIGHT: DartDetect API
echo    
echo    Game UI:      http://localhost:5000
echo    Kiosk Mode:   http://localhost:5000?kiosk=1
echo    Game API:     http://localhost:5000/swagger
echo    Detect API:   http://localhost:8000/docs
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
