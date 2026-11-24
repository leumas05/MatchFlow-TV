namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// Visual theme for the OLED-safe idle TV screensaver.
/// </summary>
public enum ScreensaverStyle
{
    SportBounce = 0,
    StadiumBoard = 1,
    Starfield = 2
}

/// <summary>
/// A user-defined negative point adjustment applied when a broadcast matches a specific term
/// (e.g. "studio", "kval", "Youth League", "damer", or a team/league/sport/service).
/// </summary>
public sealed class PenaltyRule
{
    public string Term { get; set; } = string.Empty;
    public int PenaltyPoints { get; set; } = -1000;
}

/// <summary>
/// User preferences controlling how sports, leagues, teams, penalties, services, screensaver, and quiet hours behave.
/// </summary>
public sealed class RankingProfile
{
    /// <summary>Ordered from highest to lowest priority.</summary>
    public List<string> FavoriteTeams { get; set; } = [];

    /// <summary>Ordered from highest to lowest priority.</summary>
    public List<string> FavoriteLeagues { get; set; } = [];

    /// <summary>Ordered from highest to lowest priority.</summary>
    public List<string> SportPriorities { get; set; } = [];

    /// <summary>Sports explicitly hidden/muted from automatic selection.</summary>
    public List<string> MutedSports { get; set; } = [];

    /// <summary>Tie-breaker order when two broadcasts have similar priority.</summary>
    public List<StreamingServiceId> ServicePriorities { get; set; } = [];

    /// <summary>Keywords that exclude unwanted broadcasts (e.g. "Studio", "Höjdpunkter", "Repris").</summary>
    public List<string> BlockedKeywords { get; set; } = [];

    /// <summary>Custom negative point rules that deprioritize matching broadcasts without hiding them.</summary>
    public List<PenaltyRule> PenaltyRules { get; set; } = [];

    public AutoSwitchMode AutoSwitchMode { get; set; } = AutoSwitchMode.SmartToastWithCountdown;
    public int AutoSwitchCountdownSeconds { get; set; } = 15;
    public int ScanIntervalSeconds { get; set; } = 300;
    public bool StartFullscreen { get; set; } = true;
    public bool PreventScreenSleep { get; set; } = true;

    public bool AutoTheaterMode { get; set; } = true;
    public bool DeduplicateSameMatch { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    public bool EnableIdleScreensaver { get; set; } = true;
    public ScreensaverStyle ScreensaverStyle { get; set; } = ScreensaverStyle.SportBounce;
    public int ScreensaverDelaySeconds { get; set; } = 45;
    public bool ScreensaverPureBlackMode { get; set; } = false;
    public bool AllowDisplaySleepWhenNoSport { get; set; } = false;

    public bool EnableQuietHours { get; set; } = false;
    public string QuietHoursStart { get; set; } = "23:30";
    public string QuietHoursEnd { get; set; } = "07:00";

    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdatesWhenIdle { get; set; } = true;
    public string UpdateRepository { get; set; } = string.Empty;

    /// <summary>
    /// Creates sensible Swedish TV sports defaults ready to customize.
    /// </summary>
    public static RankingProfile CreateDefault() => new()
    {
        FavoriteTeams =
        [
            "Tre Kronor",
            "Sverige"
        ],
        FavoriteLeagues =
        [
            "Champions League",
            "Allsvenskan",
            "Premier League",
            "SHL",
            "Formel 1",
            "Europa League",
            "NHL",
            "HockeyAllsvenskan"
        ],
        SportPriorities =
        [
            "Fotboll",
            "Ishockey",
            "Motorsport",
            "Vintersport",
            "Handboll",
            "Tennis",
            "Basket",
            "Innebandy",
            "Bandy",
            "Friidrott",
            "Golf",
            "Kampsport",
            "Amerikansk fotboll",
            "Cykling",
            "Snooker",
            "Övrigt"
        ],
        MutedSports = [],
        ServicePriorities =
        [
            StreamingServiceId.SvtPlay,
            StreamingServiceId.Tv4Play,
            StreamingServiceId.Viaplay,
            StreamingServiceId.HboMax,
            StreamingServiceId.Expressen,
            StreamingServiceId.PrimeVideo,
            StreamingServiceId.DisneyPlus,
            StreamingServiceId.Netflix
        ],
        BlockedKeywords =
        [
            "repris",
            "eftersnack"
        ],
        AutoSwitchMode = AutoSwitchMode.SmartToastWithCountdown,
        AutoSwitchCountdownSeconds = 15,
        ScanIntervalSeconds = 300,
        StartFullscreen = true,
        PreventScreenSleep = true,
        AutoTheaterMode = true,
        DeduplicateSameMatch = true,
        StartWithWindows = false,
        EnableIdleScreensaver = true,
        ScreensaverDelaySeconds = 45,
        ScreensaverPureBlackMode = false,
        AllowDisplaySleepWhenNoSport = false,
        EnableQuietHours = false,
        QuietHoursStart = "23:30",
        QuietHoursEnd = "07:00",
        AutoCheckUpdates = true,
        AutoInstallUpdatesWhenIdle = true,
        UpdateRepository = string.Empty
    };
}
