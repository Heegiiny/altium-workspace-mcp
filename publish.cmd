@echo off
rem Builds the server into the Release directory of the repository — run by a double click.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
pause
