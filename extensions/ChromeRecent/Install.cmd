@echo off
powershell.exe -NoProfile -File "%~dp0RegisterHost.ps1" -HostExecutable "%~dp0Host\AITaskbar.EdgeRecentHost.exe" -Browser Chrome
if errorlevel 1 exit /b %errorlevel%
echo AITaskbar Chrome companion host registered for the current user.
echo In chrome://extensions, enable Developer mode and load this unpacked folder:
echo %~dp0Browser
