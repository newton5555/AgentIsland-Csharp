@echo off
setlocal
echo ========================================================
echo   AgentIsland: Launching 1-Year Stress Test UI
echo ========================================================

echo Checking and stopping any lingering AgentIsland processes...
taskkill /f /im AgentIsland.exe >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch-StressTestUI.ps1"

endlocal
