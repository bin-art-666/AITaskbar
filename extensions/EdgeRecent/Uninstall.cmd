@echo off
powershell.exe -NoProfile -File "%~dp0RegisterHost.ps1" -HostExecutable "%~dp0Host\AITaskbar.EdgeRecentHost.exe" -Remove
if errorlevel 1 exit /b %errorlevel%
echo AITaskbar Edge companion host unregistered for the current user.
echo Remove the AITaskbar Edge extension in edge://extensions to complete removal.
