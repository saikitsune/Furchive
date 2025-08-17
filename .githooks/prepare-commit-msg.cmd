@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-commit-msg.ps1" %*
exit /b %ERRORLEVEL%
