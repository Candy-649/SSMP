@echo off
chcp 65001 >nul 2>&1
title Silksong co-op (SSMP) installer

rem Runs the real installer next to this file. -ExecutionPolicy Bypass applies to this one run only and changes
rem nothing on the machine. pwsh is preferred when present, because it handles text encoding better.

where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
)

echo.
pause
