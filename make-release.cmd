@echo off
REM Builds the distributable zip in dist\. See make-release.ps1 for details.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-release.ps1" %*
pause
