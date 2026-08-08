@echo off
rem run-xe-local-ai-engine.cmd — desktop launcher for XE Local AI Engine (Windows)
rem
rem Layout expected beside this script:
rem   publish\windows\
rem     run-xe-local-ai-engine.cmd      <- this file
rem     XE-Local-AI-Engine.Client.exe   <- self-contained binary (dotnet publish output)
rem
rem What this script does:
rem   1. Sets XE_LAUNCH_MODE=desktop so the host enters desktop mode (loopback
rem      auto-port, browser open, CTRL_CLOSE_EVENT -> graceful shutdown).
rem   2. Resolves the binary path relative to %~dp0 (the directory containing this
rem      .cmd file) so it works regardless of current working directory.
rem   3. Runs the exe in the CURRENT console window (no START command). This is
rem      required: using START would open a new window or detach the process, which
rem      breaks the CTRL_CLOSE_EVENT -> graceful shutdown chain. Closing the console
rem      window that owns this process sends CTRL_CLOSE_EVENT to the host, which is
rem      caught by SetConsoleCtrlHandler and converted to StopApplication(), triggering
rem      graceful DI disposal including llama-server child teardown via the Job Object.
rem
rem Single-instance note: only one instance at a time should be started against
rem the same user-data directory (%LOCALAPPDATA%\XE-Local-AI-Engine). Running a
rem second instance will race on the SQLite database and may corrupt data.

setlocal

set "XE_LAUNCH_MODE=desktop"
set "XE_EXE=%~dp0XE-Local-AI-Engine.Client.exe"

if not exist "%XE_EXE%" (
    echo Error: binary not found at "%XE_EXE%"
    echo Publish the app first:
    echo   dotnet publish XE-Local-AI-Engine.Client -c Release -r win-x64 -p:PublishProfile=win-x64
    echo Then copy the published binary next to this script.
    pause
    exit /b 1
)

"%XE_EXE%"
