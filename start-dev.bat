@echo off
setlocal EnableDelayedExpansion

rem Ensure Node.js is on PATH before starting the dev server.
where node >nul 2>&1
if %errorlevel% equ 0 goto :start

echo Node.js not found on PATH. Searching common install locations...
for %%d in (
    "%ProgramFiles%\nodejs"
    "%ProgramFiles(x86)%\nodejs"
    "%APPDATA%\nvm\current"
    "%LocalAppData%\Programs\nodejs"
) do (
    if exist "%%~d\node.exe" (
        set "PATH=%%~d;!PATH!"
        echo Found Node.js at %%~d
        goto :start
    )
)

echo ERROR: Node.js not found. Please install Node.js from https://nodejs.org and re-run.
pause
exit /b 1

:start
echo Starting CrossDrive dev server...
npm run start
