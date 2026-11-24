namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// A sport event annotated with its computed priority score and human-readable explanation.
/// </summary>
public sealed class RankedSportEvent
{
    public required SportEvent Event { get; init; }
    public int Score { get; init; }
    public bool IsBlockedOrMuted { get; init; }
    public string ScoreBreakdown { get; init; } = string.Empty;
    public List<string> MatchedReasons { get; init; } = [];
}

/// <summary>
/// Live telemetry reported from the embedded player WebView2.
/// </summary>
public sealed class PlaybackTelemetry
{
    public StreamingServiceId ServiceId { get; set; }
    public string PlaybackState { get; set; } = "loading"; // loading | playing | paused | ended | login_required | profile_picker | error
    public double CurrentTimeSeconds { get; set; }
    public bool HasVideoElement { get; set; }
    public bool IsMuted { get; set; }
    public string PageTitle { get; set; } = string.Empty;
    public string CurrentUrl { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
