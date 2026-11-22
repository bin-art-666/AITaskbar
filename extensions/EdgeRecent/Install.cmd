@echo off
powershell.exe -NoProfile -File "%~dp0RegisterHost.ps1" -HostExecutable "%~dp0Host\AITaskbar.EdgeRecentHost.exe"
if errorlevel 1 exit /b %errorlevel%
echo AITaskbar Edge companion host registered for the current user.
echo In the Edge profile you want to connect, open edge://extensions and use Load unpacked.
echo Select this folder:
echo %~dp0Browser
