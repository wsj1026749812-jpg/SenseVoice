@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-PiperTtsLite.ps1"
if errorlevel 1 pause
