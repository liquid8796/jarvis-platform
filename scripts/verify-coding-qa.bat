@echo off
powershell.exe -NoProfile -File "%~dp0Verify-CodingQa.ps1" %*
exit /b %errorlevel%
