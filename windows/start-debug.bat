@echo off
REM Build FluentScanner (outputs to local-SSD temp, never NAS)
REM and run the runnable app from windows\dist\FluentScanner\
cd /d "%~dp0"
set "TMPBASE=C:\Temp\opencode\fscan-build"
dotnet build source\FluentScanner.csproj -c Debug /p:BaseIntermediateOutputPath="%TMPBASE%\obj\\" /p:BaseOutputPath="%TMPBASE%\bin\\"
if errorlevel 1 pause & exit /b 1
start "" "dist\FluentScanner\FluentScanner.exe" %*
