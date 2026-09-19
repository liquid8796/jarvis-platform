@echo off
node "%~dp0test-browser-live.cjs" %*
exit /b %errorlevel%
