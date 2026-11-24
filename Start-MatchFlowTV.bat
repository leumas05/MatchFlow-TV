@echo off
cd /d "%~dp0"

if exist "dist\MatchFlowTV\MatchFlowTV.exe" (
    start "" "dist\MatchFlowTV\MatchFlowTV.exe"
    exit /b 0
)

if exist "src\AutomatiskSportTV.App\bin\Release\net10.0-windows\MatchFlowTV.exe" (
    start "" "src\AutomatiskSportTV.App\bin\Release\net10.0-windows\MatchFlowTV.exe"
    exit /b 0
)

echo Bygger och startar MatchFlow TV...
dotnet run --project "src\AutomatiskSportTV.App\AutomatiskSportTV.App.csproj" -c Release
pause
