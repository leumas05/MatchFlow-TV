namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// Normalized sports broadcast discovered from a streaming service.
/// </summary>
public sealed class SportEvent
{
    public string Id { get; set; } = string.Empty;
    public StreamingServiceId ServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RawSubtitle { get; set; } = string.Empty;
    public string Sport { get; set; } = "Övrigt";
    public string League { get; set; } = string.Empty;
    public List<string> Teams { get; set; } = [];
    public EventStatus Status { get; set; } = EventStatus.Live;
    public DateTimeOffset? StartTime { get; set; }
    public string WatchUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public DateTimeOffset DetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Builds a deterministic identifier from service, watch URL, and title.
    /// </summary>
    public static string BuildStableId(StreamingServiceId serviceId, string watchUrl, string title)
    {
        var normalizedUrl = (watchUrl ?? string.Empty).Trim().Split('?')[0].ToLowerInvariant();
        var normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
        var key = string.IsNullOrWhiteSpace(normalizedUrl)
            ? $"{serviceId}:{normalizedTitle}"
            : $"{serviceId}:{normalizedUrl}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
    }
}
