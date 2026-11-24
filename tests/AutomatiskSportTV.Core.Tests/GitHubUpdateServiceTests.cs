using System.IO.Compression;
using System.Security.Cryptography;
using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("S4m-dev/MatchFlow-TV", true)]
    [InlineData("owner/repo.net", true)]
    [InlineData("https://github.com/owner/repo", false)]
    [InlineData("owner/repo/extra", false)]
    [InlineData("", false)]
    public void IsValidRepositorySlug_ValidatesOwnerRepoFormat(string slug, bool expected)
    {
        Assert.Equal(expected, GitHubUpdateService.IsValidRepositorySlug(slug));
    }

    [Theory]
    [InlineData("https://github.com/S4m-dev/MatchFlow-TV/releases/download/v1.1.0/MatchFlowTV-win-x64.zip", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123/MatchFlowTV-win-x64.zip", true)]
    [InlineData("http://github.com/S4m-dev/MatchFlow-TV/releases/download/v1.1.0/MatchFlowTV-win-x64.zip", false)]
    [InlineData("https://evil.example/github.com/MatchFlowTV-win-x64.zip", false)]
    public void IsTrustedGitHubDownloadUrl_EnforcesHttpsGitHubHosts(string url, bool expected)
    {
        Assert.Equal(expected, GitHubUpdateService.IsTrustedGitHubDownloadUrl(url));
    }

    [Fact]
    public void ParseReleaseJson_DetectsNewerVersionAndValidatedAssets()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "draft": false,
          "html_url": "https://github.com/S4m-dev/MatchFlow-TV/releases/tag/v1.2.0",
          "body": "Bug fixes and improvements",
          "published_at": "2026-11-24T12:00:00Z",
          "assets": [
            {
              "name": "MatchFlowTV-win-x64.zip",
              "browser_download_url": "https://github.com/S4m-dev/MatchFlow-TV/releases/download/v1.2.0/MatchFlowTV-win-x64.zip"
            },
            {
              "name": "SHA256SUMS.txt",
              "browser_download_url": "https://github.com/S4m-dev/MatchFlow-TV/releases/download/v1.2.0/SHA256SUMS.txt"
            }
          ]
        }
        """;

        var updateAvailable = GitHubUpdateService.ParseReleaseJson(json, new Version(1, 0, 0));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, updateAvailable.Status);
        Assert.Equal(new Version(1, 2, 0), updateAvailable.LatestRelease?.Version);

        var alreadyUpToDate = GitHubUpdateService.ParseReleaseJson(json, new Version(1, 2, 0));
        Assert.Equal(UpdateCheckStatus.UpToDate, alreadyUpToDate.Status);
    }

    [Fact]
    public void VerifyFileSha256_AndSafeZipExtract_EnforceIntegrityAndBlockZipSlip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"astv_upd_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var goodZip = Path.Combine(tempDir, "good.zip");
            using (var archive = ZipFile.Open(goodZip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("MatchFlowTV.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("binary-content");
            }

            var actualHex = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(goodZip))).ToLowerInvariant();
            var checksumsFile = $"{actualHex}  MatchFlowTV-win-x64.zip\n";

            var parsedHex = GitHubUpdateService.ParseExpectedSha256(checksumsFile, "MatchFlowTV-win-x64.zip");
            Assert.Equal(actualHex, parsedHex);
            Assert.True(GitHubUpdateService.VerifyFileSha256(goodZip, parsedHex!));
            Assert.False(GitHubUpdateService.VerifyFileSha256(goodZip, new string('0', 64)));

            var extractDir = Path.Combine(tempDir, "extracted");
            GitHubUpdateService.ExtractZipSafely(goodZip, extractDir);
            Assert.True(File.Exists(Path.Combine(extractDir, "MatchFlowTV.exe")));

            // Zip-Slip path traversal test
            var evilZip = Path.Combine(tempDir, "evil.zip");
            using (var archive = ZipFile.Open(evilZip, ZipArchiveMode.Create))
            {
                var badEntry = archive.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(badEntry.Open());
                writer.Write("malicious");
            }

            Assert.Throws<InvalidDataException>(() =>
                GitHubUpdateService.ExtractZipSafely(evilZip, Path.Combine(tempDir, "safe_out")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
