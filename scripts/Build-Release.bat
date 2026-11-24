@echo off
cd /d "%~dp0.."
echo Bygger MatchFlow TV (fristående Windows x64-paket)...
dotnet test tests\AutomatiskSportTV.Core.Tests\AutomatiskSportTV.Core.Tests.csproj -c Release
if errorlevel 1 goto :err

if exist "dist\MatchFlowTV" rmdir /s /q "dist\MatchFlowTV"
if exist "dist\MatchFlowTV-win-x64.zip" del /q "dist\MatchFlowTV-win-x64.zip"

dotnet publish src\AutomatiskSportTV.App\AutomatiskSportTV.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist\MatchFlowTV
if errorlevel 1 goto :err

del /q "dist\MatchFlowTV\*.xml" 2>nul
copy /y README.md dist\MatchFlowTV\README.md >nul
copy /y LICENSE dist\MatchFlowTV\LICENSE >nul
copy /y NOTICE dist\MatchFlowTV\NOTICE >nul

powershell -NoProfile -Command "$ProgressPreference='SilentlyContinue'; Compress-Archive -Path 'dist\MatchFlowTV\*' -DestinationPath 'dist\MatchFlowTV-win-x64.zip' -Force; $h=(Get-FileHash 'dist\MatchFlowTV-win-x64.zip' -Algorithm SHA256).Hash; \"$h  MatchFlowTV-win-x64.zip\" | Out-File -FilePath 'dist\SHA256SUMS.txt' -Encoding ascii"
if errorlevel 1 goto :err

echo.
echo Klart! Paketet finns i: dist\MatchFlowTV-win-x64.zip
pause
exit /b 0

:err
echo.
echo Ett fel intraffade vid byggning.
pause
exit /b 1
