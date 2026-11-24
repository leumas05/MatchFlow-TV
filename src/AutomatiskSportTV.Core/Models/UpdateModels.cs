namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// Outcome of querying GitHub Releases for a newer version of MatchFlow TV.
/// </summary>
public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    Error
}

/// <summary>
/// Metadata for a validated GitHub Release package.
/// </summary>
public sealed class AppReleaseInfo
{
    public string TagName { get; init; } = string.Empty;
    public required Version Version { get; init; }
    public string ZipDownloadUrl { get; init; } = string.Empty;
    public string ChecksumsDownloadUrl { get; init; } = string.Empty;
    public string ReleasePageUrl { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAtUtc { get; init; }
}

/// <summary>
/// Result returned when checking GitHub Releases for updates.
/// </summary>
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public required Version CurrentVersion { get; init; }
    public AppReleaseInfo? LatestRelease { get; init; }
    public string Message { get; init; } = string.Empty;
}
