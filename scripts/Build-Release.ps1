param(
    [string]$Version = "1.0.0",
    [string]$GitHubRepository = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($GitHubRepository)) {
    try {
        $remoteUrl = (git remote get-url origin 2>$null)
        if ($remoteUrl -match 'github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?$') {
            $GitHubRepository = $Matches[1]
        }
    } catch {}
}

Write-Host "Kör enhetstester..." -ForegroundColor Cyan
dotnet test tests/AutomatiskSportTV.Core.Tests/AutomatiskSportTV.Core.Tests.csproj -c Release --disable-build-servers -m:1

$outDir = Join-Path $repoRoot "dist\MatchFlowTV"
$zipPath = Join-Path $repoRoot "dist\MatchFlowTV-win-x64.zip"
$sumPath = Join-Path $repoRoot "dist\SHA256SUMS.txt"

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Bygger fristående Windows x64-paket (v$Version, repo: $GitHubRepository)..." -ForegroundColor Cyan
dotnet publish src/AutomatiskSportTV.App/AutomatiskSportTV.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --disable-build-servers -m:1 `
    -p:UseSharedCompilation=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:GitHubRepository=$GitHubRepository `
    -o $outDir

$ProgressPreference = "SilentlyContinue"
Remove-Item "$outDir\*.xml" -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repoRoot "README.md") (Join-Path $outDir "README.md") -Force
Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $outDir "LICENSE") -Force
Copy-Item (Join-Path $repoRoot "NOTICE") (Join-Path $outDir "NOTICE") -Force

Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
"$hash  MatchFlowTV-win-x64.zip" | Out-File -FilePath $sumPath -Encoding ascii

Write-Host ""
Write-Host "Klart! Redo att ladda upp till GitHub Releases:" -ForegroundColor Green
Write-Host "  - $zipPath"
Write-Host "  - $sumPath"
