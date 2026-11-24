using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Scores and sorts detected sport events according to the user's team, league, sport, and service priorities.
/// </summary>
public sealed class RankingEngine
{
    private static readonly Dictionary<string, string[]> TeamAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sverige"] = ["Sweden", "Tre Kronor", "Blågult"],
        ["Tre Kronor"] = ["Sverige", "Sweden"],
        ["Manchester United"] = ["Man United", "Man Utd"],
        ["Manchester City"] = ["Man City"],
        ["Malmö FF"] = ["MFF"],
        ["IFK Göteborg"] = ["Blåvitt"]
    };

    /// <summary>
    /// Scores and returns events ordered from highest priority to lowest.
    /// </summary>
    public IReadOnlyList<RankedSportEvent> Rank(
        IEnumerable<SportEvent> events,
        RankingProfile profile,
        IReadOnlySet<string>? skippedEventIds = null)
    {
        var results = new List<RankedSportEvent>();

        foreach (var evt in events)
        {
            SportsKnowledgeBase.Enrich(evt);
            results.Add(ScoreSingle(evt, profile, skippedEventIds));
        }

        var ordered = results
            .OrderBy(r => r.IsBlockedOrMuted)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => r.Event.StartTime ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Event.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return profile.DeduplicateSameMatch
            ? EventDeduplicator.Deduplicate(ordered)
            : ordered;
    }

    private static RankedSportEvent ScoreSingle(
        SportEvent evt,
        RankingProfile profile,
        IReadOnlySet<string>? skippedEventIds)
    {
        var searchable = $"{evt.Title} {evt.RawSubtitle} {evt.League} {evt.Sport} {evt.ServiceName} {string.Join(' ', evt.Teams)}";
        var reasons = new List<string>();
        var blocked = false;

        if (skippedEventIds is not null && skippedEventIds.Contains(evt.Id))
        {
            return new RankedSportEvent
            {
                Event = evt,
                Score = -100_000,
                IsBlockedOrMuted = true,
                ScoreBreakdown = "Överhoppad i denna session",
                MatchedReasons = ["Överhoppad"]
            };
        }

        if (profile.MutedSports.Any(s => s.Equals(evt.Sport, StringComparison.OrdinalIgnoreCase)))
        {
            blocked = true;
            reasons.Add($"Dold sport: {evt.Sport}");
        }

        foreach (var keyword in profile.BlockedKeywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword) &&
                searchable.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                blocked = true;
                reasons.Add($"Blockerat ord: {keyword}");
                break;
            }
        }

        if (blocked)
        {
            return new RankedSportEvent
            {
                Event = evt,
                Score = -90_000,
                IsBlockedOrMuted = true,
                ScoreBreakdown = string.Join(" • ", reasons),
                MatchedReasons = reasons
            };
        }

        var score = 0;

        if (evt.Status == EventStatus.Live)
        {
            score += 10_000;
        }
        else if (evt.Status == EventStatus.Ended)
        {
            return new RankedSportEvent
            {
                Event = evt,
                Score = -50_000,
                IsBlockedOrMuted = true,
                ScoreBreakdown = "Sändning avslutad",
                MatchedReasons = ["Avslutad"]
            };
        }

        // 1. Favorite teams (highest impact)
        var matchedTeamsCount = 0;
        for (var i = 0; i < profile.FavoriteTeams.Count; i++)
        {
            var team = profile.FavoriteTeams[i];
            if (MatchesWithAliases(searchable, team))
            {
                if (matchedTeamsCount == 0)
                {
                    var pts = Math.Max(800, 3_000 - i * 120);
                    score += pts;
                    reasons.Add($"Lag: {team} (+{pts})");
                }
                else
                {
                    score += 450;
                    reasons.Add($"Lag #2: {team} (+450)");
                }
                matchedTeamsCount++;
            }
        }

        // 2. Favorite leagues
        for (var i = 0; i < profile.FavoriteLeagues.Count; i++)
        {
            var league = profile.FavoriteLeagues[i];
            if (evt.League.Equals(league, StringComparison.OrdinalIgnoreCase) ||
                searchable.Contains(league, StringComparison.OrdinalIgnoreCase))
            {
                var pts = Math.Max(250, 1_200 - i * 50);
                score += pts;
                reasons.Add($"Liga: {league} (+{pts})");
                break;
            }
        }

        // 3. Sport priority
        var sportMatched = false;
        for (var i = 0; i < profile.SportPriorities.Count; i++)
        {
            var sport = profile.SportPriorities[i];
            if (evt.Sport.Equals(sport, StringComparison.OrdinalIgnoreCase))
            {
                var pts = Math.Max(40, 600 - i * 35);
                score += pts;
                reasons.Add($"Sport: {sport} (+{pts})");
                sportMatched = true;
                break;
            }
        }

        if (!sportMatched)
        {
            score += 20;
            reasons.Add($"Sport: {evt.Sport} (+20)");
        }

        // 4. Service tie-breaker
        for (var i = 0; i < profile.ServicePriorities.Count; i++)
        {
            if (profile.ServicePriorities[i] == evt.ServiceId)
            {
                var pts = Math.Max(10, 80 - i * 10);
                score += pts;
                reasons.Add($"{evt.ServiceName} (+{pts})");
                break;
            }
        }

        // 5. Custom negative point penalties (deprioritize without completely blocking)
        if (profile.PenaltyRules is not null)
        {
            foreach (var rule in profile.PenaltyRules)
            {
                if (string.IsNullOrWhiteSpace(rule.Term) || rule.PenaltyPoints == 0)
                {
                    continue;
                }

                var term = rule.Term.Trim();
                if (MatchesWithAliases(searchable, term))
                {
                    var penalty = -Math.Clamp(Math.Abs(rule.PenaltyPoints), 50, 20_000);
                    score += penalty;
                    reasons.Add($"Minus: {term} ({penalty})");
                }
            }
        }

        return new RankedSportEvent
        {
            Event = evt,
            Score = score,
            IsBlockedOrMuted = false,
            ScoreBreakdown = string.Join(" • ", reasons),
            MatchedReasons = reasons
        };
    }

    private static bool MatchesWithAliases(string haystack, string team)
    {
        if (string.IsNullOrWhiteSpace(team))
        {
            return false;
        }

        if (haystack.Contains(team.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TeamAliases.TryGetValue(team.Trim(), out var aliases))
        {
            return aliases.Any(alias => haystack.Contains(alias, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }
}
