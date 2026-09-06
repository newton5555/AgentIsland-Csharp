@echo off
setlocal
echo ========================================================
echo   AgentIsland: Launching 1-Year Stress Test UI
echo ========================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch-StressTestUI.ps1"

endlocal
