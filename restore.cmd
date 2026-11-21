@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dotnet-entrypoint.ps1" restore %*
exit /b %errorlevel%
