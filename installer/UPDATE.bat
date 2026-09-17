@echo off
rem Double-click this to pull the newest co-op build. It only replaces the plugin of an install that already
rem works; run INSTALL.bat first if the mod is not installed yet.
rem
rem -ExecutionPolicy Bypass applies to this one run only and changes no system setting. pwsh is preferred
rem because Windows PowerShell 5.1 mangles UTF-8 without a BOM.
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*
)
echo.
pause
