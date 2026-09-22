@echo off
rem Start the native window by default; --browser and --headless keep the standalone engine.
setlocal
set "XE_LAUNCH_MODE=desktop"
set "XE_EXE=%~dp0XE-Local-AI-Engine.WindowsLauncher.exe"
if not exist "%XE_EXE%" (
    echo Error: launcher not found at "%XE_EXE%". Extract the complete Windows package.
    pause
    exit /b 1
)
"%XE_EXE%" %*
exit /b %ERRORLEVEL%
