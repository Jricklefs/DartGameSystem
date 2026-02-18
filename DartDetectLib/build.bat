@echo off
echo Building DartDetectLib...
cd /d %~dp0
if not exist build mkdir build
cd build
C:\Users\clawd\cmake\cmake-3.31.6-windows-x86_64\bin\cmake.exe -G "Visual Studio 17 2022" -A x64 ..
C:\Users\clawd\cmake\cmake-3.31.6-windows-x86_64\bin\cmake.exe --build . --config Release
if %ERRORLEVEL% EQU 0 (
    echo.
    echo BUILD SUCCESS
    echo DLL: %cd%\bin\Release\DartDetectLib.dll
    copy /Y C:\Users\clawd\opencv\build\x64\vc16\bin\opencv_world4110.dll bin\Release\ >nul
    echo OpenCV DLL copied
) else (
    echo BUILD FAILED
)
