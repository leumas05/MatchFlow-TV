using System.Diagnostics;
using System.IO;
using System.Reflection;
using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;

namespace AutomatiskSportTV.App.Services;

/// <summary>
/// Coordinates checking GitHub Releases, staging SHA-256 verified updates, and atomically
/// swapping MatchFlowTV.exe after graceful shutdown while preserving user logins and config.
/// </summary>
public sealed class AppUpdateManager
{
    private readonly string _updatesRoot;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public AppUpdateManager()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _updatesRoot = Path.Combine(localAppData, "AutomatiskSportTV", "Updates");
        DirectorySecurityHelper.RestrictDirectoryToCurrentUser(_updatesRoot);

        CurrentVersion = ReadCurrentVersion();
        BakedRepository = ReadBakedGitHubRepository();
    }

    public Version CurrentVersion { get; }
    public string BakedRepository { get; }
    public AppReleaseInfo? StagedRelease { get; private set; }
    public string? StagedDirectory { get; private set; }
    public string StatusSummary { get; private set; } = "Ej kontrollerad";

    public bool HasStagedUpdate => StagedRelease is not null && !string.IsNullOrWhiteSpace(StagedDirectory) && Directory.Exists(StagedDirectory);

    /// <summary>
    /// Returns true only when running from a packaged standalone MatchFlowTV.exe outside a dev bin\Debug/Release tree.
    /// </summary>
    public bool CanSelfReplaceExecutable
    {
        get
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            if (!Path.GetFileName(exePath).Equals("MatchFlowTV.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = exePath.Replace('/', '\\');
            if (normalized.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
    }

    public string ResolveRepositorySlug(string? userConfiguredRepo)
    {
        if (GitHubUpdateService.IsValidRepositorySlug(userConfiguredRepo))
        {
            return userConfiguredRepo!.Trim();
        }
        return BakedRepository;
    }

    /// <summary>
    /// Checks GitHub Releases and, if a newer release exists, downloads and SHA-256 verifies it into staging.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAndStageAsync(string? userConfiguredRepo, CancellationToken ct = default)
    {
        await _checkLock.WaitAsync(ct);
        try
        {
            var repoSlug = ResolveRepositorySlug(userConfiguredRepo);
            StatusSummary = "Söker efter uppdateringar...";

            var check = await GitHubUpdateService.CheckForUpdateAsync(repoSlug, CurrentVersion, ct: ct);
            if (check.Status != UpdateCheckStatus.UpdateAvailable || check.LatestRelease is null)
            {
                StatusSummary = check.Message;
                return check;
            }

            // Already staged this exact version
            if (StagedRelease is not null &&
                StagedRelease.Version == check.LatestRelease.Version &&
                HasStagedUpdate)
            {
                StatusSummary = $"Uppdatering {StagedRelease.TagName} nedladdad & verifierad";
                return check;
            }

            StatusSummary = $"Laddar ner & verifierar {check.LatestRelease.TagName}...";
            var (success, stagingDir, error) = await GitHubUpdateService.DownloadAndStageUpdateAsync(
                check.LatestRelease,
                _updatesRoot,
                CurrentVersion,
                ct: ct);

            if (!success || string.IsNullOrWhiteSpace(stagingDir))
            {
                StatusSummary = error ?? "Kunde inte förbereda uppdatering.";
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = CurrentVersion,
                    LatestRelease = check.LatestRelease,
                    Message = StatusSummary
                };
            }

            StagedRelease = check.LatestRelease;
            StagedDirectory = stagingDir;
            StatusSummary = $"Uppdatering {StagedRelease.TagName} redo att installeras";
            return check;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>
    /// Spawns a detached Windows PowerShell swap helper using environment variables (zero path interpolation)
    /// that waits for this process to exit, copies verified files over the install folder, and relaunches MatchFlowTV.exe.
    /// </summary>
    public bool TryLaunchStagedUpdateInstaller()
    {
        if (!HasStagedUpdate || !CanSelfReplaceExecutable)
        {
            return false;
        }

        var exePath = Environment.ProcessPath!;
        var installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return false;
        }

        // Static script reading all paths strictly from process environment variables
        const string swapScript = """
            $pidToWait = [int]$env:ASTV_PID
            $src = $env:ASTV_SRC
            $dst = $env:ASTV_DST
            $exe = $env:ASTV_EXE
            try { Wait-Process -Id $pidToWait -Timeout 25 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 400
            for ($attempt = 0; $attempt -lt 12; $attempt++) {
                try {
                    Get-ChildItem -Path $src -Force | ForEach-Object {
                        Copy-Item -Path $_.FullName -Destination (Join-Path $dst $_.Name) -Recurse -Force
                    }
                    break
                } catch {
                    Start-Sleep -Milliseconds 600
                }
            }
            if (Test-Path $exe) {
                Start-Process -FilePath $exe -WorkingDirectory $dst
            }
            $versionFolder = Split-Path -Parent $src
            if ($versionFolder -and (Test-Path $versionFolder)) {
                Remove-Item -Path $versionFolder -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(swapScript);

        psi.Environment["ASTV_PID"] = Environment.ProcessId.ToString();
        psi.Environment["ASTV_SRC"] = StagedDirectory!;
        psi.Environment["ASTV_DST"] = installDir;
        psi.Environment["ASTV_EXE"] = exePath;

        Process.Start(psi);
        return true;
    }

    private static Version ReadCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? new Version(1, 0, 0) : new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    private static string ReadBakedGitHubRepository()
    {
        var attrs = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>();
        var match = attrs.FirstOrDefault(a => string.Equals(a.Key, "GitHubRepository", StringComparison.OrdinalIgnoreCase));
        return match?.Value?.Trim() ?? string.Empty;
    }
}
