using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Checks GitHub Releases for newer versions of MatchFlow TV, downloads release packages over HTTPS
/// from verified GitHub hosts, verifies mandatory SHA-256 checksums, and safely extracts updates.
/// </summary>
public static partial class GitHubUpdateService
{
    public const string ReleaseZipAssetName = "MatchFlowTV-win-x64.zip";
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    private static readonly string[] TrustedGitHubHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    public static bool IsValidRepositorySlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && RepoSlugRegex().IsMatch(slug.Trim());

    public static bool IsTrustedGitHubDownloadUrl(string? url)
    {
        if (!UrlSecurityGuard.IsSafeHttpsUrl(url) || !Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        return TrustedGitHubHosts.Any(allowed =>
            host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }

    public static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Trim().TrimStart('v', 'V');
        var cutIdx = cleaned.IndexOfAny(['-', '+', ' ']);
        if (cutIdx >= 0)
        {
            cleaned = cleaned[..cutIdx];
        }

        if (!cleaned.Contains('.'))
        {
            cleaned += ".0";
        }

        return Version.TryParse(cleaned, out var parsed) ? NormalizeVersion(parsed) : null;
    }

    public static bool IsNewerVersion(Version current, Version candidate) =>
        NormalizeVersion(candidate) > NormalizeVersion(current);

    /// <summary>
    /// Queries GitHub Releases API for the latest release of the configured owner/repo.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        string? repositorySlug,
        Version currentVersion,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var normalizedCurrent = NormalizeVersion(currentVersion);
        if (!IsValidRepositorySlug(repositorySlug))
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.NotConfigured,
                CurrentVersion = normalizedCurrent,
                Message = "GitHub-repository ej konfigurerat ännu (sätts automatiskt vid GitHub Release)."
            };
        }

        var slug = repositorySlug!.Trim();
        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateHttpClient(normalizedCurrent);

        try
        {
            var apiUrl = $"https://api.github.com/repos/{slug}/releases/latest";
            using var response = await client.GetAsync(apiUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = normalizedCurrent,
                    Message = $"Kunde inte kontakta GitHub Releases (HTTP {(int)response.StatusCode})."
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseReleaseJson(json, normalizedCurrent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Error,
                CurrentVersion = normalizedCurrent,
                Message = $"Uppdateringskontroll misslyckades: {ex.Message}"
            };
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Parses a GitHub Releases API JSON payload and validates release assets and trusted download URLs.
    /// </summary>
    public static UpdateCheckResult ParseReleaseJson(string json, Version currentVersion)
    {
        var normalizedCurrent = NormalizeVersion(currentVersion);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean())
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CurrentVersion = normalizedCurrent,
                    Message = "Senaste versionen är redan installerad."
                };
            }

            var tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            var latestVersion = ParseVersion(tagName);
            if (latestVersion is null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = normalizedCurrent,
                    Message = $"Ogiltig versionstagg i GitHub Release: '{tagName}'."
                };
            }

            if (!IsNewerVersion(normalizedCurrent, latestVersion))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CurrentVersion = normalizedCurrent,
                    Message = $"Du har redan senaste versionen (v{normalizedCurrent})."
                };
            }

            string? zipUrl = null;
            string? checksumsUrl = null;

            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;

                    if (string.Equals(name, ReleaseZipAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url;
                    }
                    else if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        checksumsUrl = url;
                    }
                }
            }

            if (!IsTrustedGitHubDownloadUrl(zipUrl) || !IsTrustedGitHubDownloadUrl(checksumsUrl))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = normalizedCurrent,
                    Message = $"Release {tagName} saknar giltig {ReleaseZipAssetName} eller {ChecksumsAssetName}."
                };
            }

            var htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? string.Empty : string.Empty;
            var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;
            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("published_at", out var pubEl) && DateTimeOffset.TryParse(pubEl.GetString(), out var parsedPub))
            {
                publishedAt = parsedPub;
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentVersion = normalizedCurrent,
                LatestRelease = new AppReleaseInfo
                {
                    TagName = tagName,
                    Version = latestVersion,
                    ZipDownloadUrl = zipUrl!,
                    ChecksumsDownloadUrl = checksumsUrl!,
                    ReleasePageUrl = IsTrustedGitHubDownloadUrl(htmlUrl) ? htmlUrl : string.Empty,
                    ReleaseNotes = body,
                    PublishedAtUtc = publishedAt
                },
                Message = $"Ny version tillgänglig: {tagName}"
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Error,
                CurrentVersion = normalizedCurrent,
                Message = $"Kunde inte läsa release-data: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Downloads SHA256SUMS.txt and the release zip, verifies the SHA-256 hash in constant time,
    /// and extracts the update safely into a staging directory.
    /// </summary>
    public static async Task<(bool Success, string? StagingDir, string? ErrorMessage)> DownloadAndStageUpdateAsync(
        AppReleaseInfo release,
        string updatesRootDir,
        Version currentVersion,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        if (!IsTrustedGitHubDownloadUrl(release.ZipDownloadUrl) ||
            !IsTrustedGitHubDownloadUrl(release.ChecksumsDownloadUrl))
        {
            return (false, null, "Blockerad: nedladdningslänken pekar inte på en betrodd GitHub-domän.");
        }

        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateHttpClient(currentVersion);

        try
        {
            Directory.CreateDirectory(updatesRootDir);
            var versionFolder = Path.Combine(updatesRootDir, $"v{release.Version}");
            var stagingDir = Path.Combine(versionFolder, "staging");
            var zipPath = Path.Combine(versionFolder, ReleaseZipAssetName);

            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            Directory.CreateDirectory(versionFolder);

            var checksumsText = await client.GetStringAsync(release.ChecksumsDownloadUrl, ct);
            var expectedSha256 = ParseExpectedSha256(checksumsText, ReleaseZipAssetName);
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return (false, null, $"Hittade ingen giltig SHA-256-kontrollsumma för {ReleaseZipAssetName}.");
            }

            using (var downloadResponse = await client.GetAsync(release.ZipDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                downloadResponse.EnsureSuccessStatusCode();
                await using var netStream = await downloadResponse.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(zipPath);
                await netStream.CopyToAsync(fileStream, ct);
            }

            if (!VerifyFileSha256(zipPath, expectedSha256))
            {
                File.Delete(zipPath);
                return (false, null, "SHA-256-verifieringen misslyckades — uppdateringen avbröts av säkerhetsskäl.");
            }

            ExtractZipSafely(zipPath, stagingDir);

            var stagedExe = Path.Combine(stagingDir, "MatchFlowTV.exe");
            if (!File.Exists(stagedExe))
            {
                return (false, null, "Uppdateringspaketet saknar MatchFlowTV.exe.");
            }

            return (true, stagingDir, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, $"Kunde inte ladda ner uppdatering: {ex.Message}");
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    public static string? ParseExpectedSha256(string checksumsContent, string targetFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumsContent))
        {
            return null;
        }

        var lines = checksumsContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var match = ChecksumLineRegex().Match(line.Trim());
            if (match.Success &&
                string.Equals(match.Groups[2].Value.Trim(), targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups[1].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    public static bool VerifyFileSha256(string filePath, string expectedHex)
    {
        if (!File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedHex) || expectedHex.Length != 64)
        {
            return false;
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHex);
        }
        catch
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var actualBytes = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    /// <summary>
    /// Extracts a zip archive while rejecting any entry attempting Zip-Slip path traversal.
    /// </summary>
    public static void ExtractZipSafely(string zipPath, string destinationDir)
    {
        var fullDestRoot = Path.GetFullPath(destinationDir);
        if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDestRoot += Path.DirectorySeparatorChar;
        }
        Directory.CreateDirectory(fullDestRoot);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.FullName));
            if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Osäker sökväg i zip-arkiv avvisad: {entry.FullName}");
            }

            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static HttpClient CreateHttpClient(Version currentVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MatchFlowTV/{currentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version NormalizeVersion(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoSlugRegex();

    [GeneratedRegex(@"^([A-Fa-f0-9]{64})\s+\*?(.+)$")]
    private static partial Regex ChecksumLineRegex();
}
