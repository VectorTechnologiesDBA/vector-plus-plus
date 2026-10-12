@echo off
rem Runs build.ps1 without changing the system's PowerShell execution policy (the bypass applies to this run only).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
