@echo off
powershell.exe -ExecutionPolicy Bypass -File "%~dp0Deploy-SherpaStreamingAsr.ps1"
if errorlevel 1 pause
