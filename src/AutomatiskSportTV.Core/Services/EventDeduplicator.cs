using System.Text.RegularExpressions;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Deduplicates identical matches aired simultaneously across multiple streaming services,
/// keeping the highest-ranked broadcast and annotating alternative services.
/// </summary>
public static partial class EventDeduplicator
{
    /// <summary>
    /// Collapses duplicate broadcasts from an already priority-sorted ranked event list.
    /// </summary>
    public static IReadOnlyList<RankedSportEvent> Deduplicate(IReadOnlyList<RankedSportEvent> sortedEvents)
    {
        var kept = new List<RankedSportEvent>(sortedEvents.Count);
        var indexBySignature = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in sortedEvents)
        {
            if (item.IsBlockedOrMuted)
            {
                kept.Add(item);
                continue;
            }

            var signature = BuildMatchSignature(item.Event);
            if (string.IsNullOrWhiteSpace(signature))
            {
                kept.Add(item);
                continue;
            }

            if (indexBySignature.TryGetValue(signature, out var existingIdx))
            {
                var primary = kept[existingIdx];
                if (primary.Event.ServiceId != item.Event.ServiceId &&
                    !primary.ScoreBreakdown.Contains(item.Event.ServiceName, StringComparison.OrdinalIgnoreCase))
                {
                    kept[existingIdx] = new RankedSportEvent
                    {
                        Event = primary.Event,
                        Score = primary.Score,
                        IsBlockedOrMuted = primary.IsBlockedOrMuted,
                        MatchedReasons = primary.MatchedReasons,
                        ScoreBreakdown = $"{primary.ScoreBreakdown} • även på {item.Event.ServiceName}"
                    };
                }
                continue;
            }

            indexBySignature[signature] = kept.Count;
            kept.Add(item);
        }

        return kept;
    }

    internal static string BuildMatchSignature(SportEvent evt)
    {
        var statusPrefix = evt.Status.ToString();

        if (evt.Teams.Count >= 2)
        {
            var normalizedTeams = evt.Teams
                .Take(2)
                .Select(NormalizeToken)
                .Where(t => t.Length >= 2)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalizedTeams.Length == 2)
            {
                return $"{statusPrefix}:{NormalizeToken(evt.Sport)}:{normalizedTeams[0]}|{normalizedTeams[1]}";
            }
        }

        var cleanedTitle = CleanTitleForDedup(evt.Title);
        return cleanedTitle.Length >= 6
            ? $"{statusPrefix}:{NormalizeToken(evt.Sport)}:{cleanedTitle}"
            : string.Empty;
    }

    private static string CleanTitleForDedup(string title)
    {
        var stripped = PrefixNoiseRegex().Replace(title ?? string.Empty, string.Empty);
        stripped = BracketNoiseRegex().Replace(stripped, string.Empty);
        return NormalizeToken(stripped);
    }

    private static string NormalizeToken(string input)
    {
        var collapsed = NonAlphanumericRegex().Replace((input ?? string.Empty).ToLowerInvariant(), " ");
        return MultiSpaceRegex().Replace(collapsed, " ").Trim();
    }

    [GeneratedRegex(@"^(live|direkt|pågår|nu|hd|4k|uhd|studio)\s*[:\-–|•]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixNoiseRegex();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
