@echo off
REM Rebuilds the standalone copy the desktop "Clip Editor" shortcut launches.
REM Run this after making code changes you want the shortcut to pick up.
cd /d "%~dp0"
dotnet publish ClipEditor\ClipEditor.csproj -c Release -o "%~dp0app" --nologo
echo.
echo Done. Desktop shortcut now launches the updated build.
pause
