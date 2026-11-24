namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// Per-service settings, URLs, and runtime scan diagnostics.
/// </summary>
public sealed class ServiceConfig
{
    public StreamingServiceId ServiceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AccentColorHex { get; set; } = "#22C55E";
    public bool Enabled { get; set; } = true;
    public bool RequiresLogin { get; set; } = true;
    public string SportsHubUrl { get; set; } = string.Empty;
    public string LoginUrl { get; set; } = string.Empty;
    public int VolumePercent { get; set; } = 100;
    public ServiceAuthState AuthState { get; set; } = ServiceAuthState.Unknown;
    public DateTimeOffset? LastScanUtc { get; set; }
    public int LastLiveCount { get; set; }
    public int LastUpcomingCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Resolves dynamic placeholders such as {today} (yyyy-MM-dd in local time) inside SportsHubUrl.
    /// </summary>
    public string ResolveSportsHubUrl(DateTimeOffset? localNow = null)
    {
        var now = localNow ?? DateTimeOffset.Now;
        return (SportsHubUrl ?? string.Empty).Replace("{today}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the default configurations for all supported Swedish streaming services.
    /// </summary>
    public static List<ServiceConfig> CreateDefaults() =>
    [
        new()
        {
            ServiceId = StreamingServiceId.SvtPlay,
            DisplayName = "SVT Play",
            AccentColorHex = "#E11D48",
            Enabled = true,
            RequiresLogin = false,
            SportsHubUrl = "https://www.svtplay.se/kategori/sport?tab=schedule&scheduleCategory=all",
            LoginUrl = "https://www.svtplay.se/konto",
            AuthState = ServiceAuthState.NotRequired
        },
        new()
        {
            ServiceId = StreamingServiceId.Tv4Play,
            DisplayName = "TV4 Play",
            AccentColorHex = "#EF4444",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://www.tv4play.se/sport/tabla?d={today}",
            LoginUrl = "https://www.tv4play.se/logga-in",
            AuthState = ServiceAuthState.Unknown
        },
        new()
        {
            ServiceId = StreamingServiceId.Viaplay,
            DisplayName = "Viaplay",
            AccentColorHex = "#F43F5E",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://viaplay.se/sport#sport-schedule-top",
            LoginUrl = "https://viaplay.se/login",
            AuthState = ServiceAuthState.Unknown
        },
        new()
        {
            ServiceId = StreamingServiceId.DisneyPlus,
            DisplayName = "Disney+",
            AccentColorHex = "#3B82F6",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://www.disneyplus.com/sv-se/browse/page-1eaf1f02-deb4-48d0-b54f-cbf7a843e8f3",
            LoginUrl = "https://www.disneyplus.com/login",
            AuthState = ServiceAuthState.Unknown
        },
        new()
        {
            ServiceId = StreamingServiceId.PrimeVideo,
            DisplayName = "Prime Video",
            AccentColorHex = "#0EA5E9",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://www.primevideo.com/sports",
            LoginUrl = "https://www.primevideo.com/signin",
            AuthState = ServiceAuthState.Unknown
        },
        new()
        {
            ServiceId = StreamingServiceId.Netflix,
            DisplayName = "Netflix",
            AccentColorHex = "#DC2626",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://www.netflix.com/browse",
            LoginUrl = "https://www.netflix.com/login",
            AuthState = ServiceAuthState.Unknown
        },
        new()
        {
            ServiceId = StreamingServiceId.Expressen,
            DisplayName = "Expressen",
            AccentColorHex = "#C81E1E",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://livesport.expressen.se/sv/live",
            LoginUrl = "https://livesport.expressen.se/sv/live",
            AuthState = ServiceAuthState.Unknown
        },
        new()
        {
            ServiceId = StreamingServiceId.HboMax,
            DisplayName = "HBO Max",
            AccentColorHex = "#7C3AED",
            Enabled = true,
            RequiresLogin = true,
            SportsHubUrl = "https://play.hbomax.com/sport",
            LoginUrl = "https://play.hbomax.com/login",
            AuthState = ServiceAuthState.Unknown
        }
    ];
}
