namespace AutomatiskSportTV.Core.Models;

/// <summary>
/// Live match play phase used for smart halftime/intermission switching and automatic return.
/// </summary>
public enum MatchPlayPhase
{
    Unknown = 0,
    Scheduled = 1,
    InPlay = 2,
    HalftimeOrBreak = 3,
    Finished = 4
}

/// <summary>
/// Real-time scoreboard and play-phase snapshot for a single sporting event.
/// </summary>
public sealed class MatchLiveStatus
{
    public MatchPlayPhase Phase { get; init; } = MatchPlayPhase.Unknown;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string HomeScore { get; init; } = string.Empty;
    public string AwayScore { get; init; } = string.Empty;
    public string ClockDisplay { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public int Period { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string FormatScoreline() =>
        !string.IsNullOrWhiteSpace(HomeScore) && !string.IsNullOrWhiteSpace(AwayScore)
            ? $"{HomeScore}–{AwayScore}"
            : string.Empty;

    public string FormatBadgeText()
    {
        var score = FormatScoreline();
        var phaseLabel = Phase switch
        {
            MatchPlayPhase.HalftimeOrBreak => string.IsNullOrWhiteSpace(DetailText) ? "Paus" : DetailText,
            MatchPlayPhase.Finished => "Slut",
            MatchPlayPhase.InPlay => !string.IsNullOrWhiteSpace(ClockDisplay) ? ClockDisplay : DetailText,
            _ => DetailText
        };

        if (!string.IsNullOrWhiteSpace(score) && !string.IsNullOrWhiteSpace(phaseLabel))
        {
            return $"{score}  •  {phaseLabel}";
        }
        return !string.IsNullOrWhiteSpace(score) ? score : phaseLabel;
    }
}
