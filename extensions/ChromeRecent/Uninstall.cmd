@echo off
powershell.exe -NoProfile -File "%~dp0RegisterHost.ps1" -HostExecutable "%~dp0Host\AITaskbar.EdgeRecentHost.exe" -Browser Chrome -Remove
if errorlevel 1 exit /b %errorlevel%
echo AITaskbar Chrome companion host unregistered for the current user.
echo Remove the AITaskbar Chrome extension in chrome://extensions to complete removal.
