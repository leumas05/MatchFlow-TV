namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// Supported streaming platforms integrated into Automatisk Sport TV.
/// </summary>
public enum StreamingServiceId
{
    SvtPlay,
    Tv4Play,
    Viaplay,
    DisneyPlus,
    PrimeVideo,
    Netflix,
    Expressen,
    HboMax
}

/// <summary>
/// Authentication status detected for a streaming service session.
/// </summary>
public enum ServiceAuthState
{
    Unknown,
    NotRequired,
    Authenticated,
    LoginRequired,
    ProfileSelectionRequired,
    RateLimitedOrChallenge,
    Error
}

/// <summary>
/// Live broadcast status for a detected sports event.
/// </summary>
public enum EventStatus
{
    Live,
    Upcoming,
    Ended
}

/// <summary>
/// Behavior when a higher-ranked live event starts during active playback.
/// </summary>
public enum AutoSwitchMode
{
    /// <summary>Show a countdown toast offering to switch automatically unless cancelled.</summary>
    SmartToastWithCountdown,

    /// <summary>Immediately switch channels when a higher-priority live stream begins.</summary>
    ImmediateAutoSwitch,

    /// <summary>Only switch automatically when the current broadcast ends or user skips.</summary>
    OnlyWhenCurrentEnds
}
