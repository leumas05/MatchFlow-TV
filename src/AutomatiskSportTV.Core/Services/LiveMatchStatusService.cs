using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Queries public HTTPS live scoreboard feeds (ESPN site API) to detect exact match phase
/// (InPlay, Halftime/PeriodBreak, Finished), clock, and live score for the currently watched match
/// and any primary match paused during halftime/intermission.
/// </summary>
public sealed partial class LiveMatchStatusService
{
    private sealed record CachedBoard(DateTimeOffset FetchedAtUtc, string RawJson);

    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly Dictionary<string, CachedBoard> _boardCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private static readonly Dictionary<string, string[]> LeagueEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Champions League"] = ["soccer/uefa.champions"],
        ["Europa League"] = ["soccer/uefa.europa"],
        ["Conference League"] = ["soccer/uefa.europa.conf"],
        ["Premier League"] = ["soccer/eng.1"],
        ["EFL Championship"] = ["soccer/eng.2"],
        ["Allsvenskan"] = ["soccer/swe.1"],
        ["Superettan"] = ["soccer/swe.2"],
        ["La Liga"] = ["soccer/esp.1"],
        ["Serie A"] = ["soccer/ita.1"],
        ["Bundesliga"] = ["soccer/ger.1"],
        ["Ligue 1"] = ["soccer/fra.1"],
        ["Nations League"] = ["soccer/uefa.nations"],
        ["Fotbolls-VM"] = ["soccer/fifa.world"],
        ["Fotbolls-EM"] = ["soccer/uefa.euro"],
        ["NHL"] = ["hockey/nhl"],
        ["NBA"] = ["basketball/nba"],
        ["WNBA"] = ["basketball/wnba"],
        ["NCAA Basketball"] = ["basketball/mens-college-basketball", "basketball/womens-college-basketball"],
        ["NFL"] = ["football/nfl"],
        ["NCAA Football"] = ["football/college-football"]
    };

    private static readonly Dictionary<string, string[]> SportFallbackEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fotboll"] = ["soccer/uefa.champions", "soccer/uefa.europa", "soccer/uefa.europa.conf", "soccer/eng.1", "soccer/esp.1", "soccer/ita.1", "soccer/ger.1", "soccer/swe.1"],
        ["Ishockey"] = ["hockey/nhl"],
        ["Basket"] = ["basketball/nba", "basketball/mens-college-basketball", "basketball/womens-college-basketball"],
        ["Amerikansk fotboll"] = ["football/nfl", "football/college-football"]
    };

    private static readonly Dictionary<string, string[]> TeamNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["man city"] = ["manchester city"],
        ["man united"] = ["manchester united", "man utd"],
        ["man utd"] = ["manchester united", "man united"],
        ["bayern munchen"] = ["bayern munich"],
        ["psg"] = ["paris saint germain", "paris sg"],
        ["inter"] = ["internazionale", "inter milan"],
        ["ac milan"] = ["milan"],
        ["tottenham"] = ["tottenham hotspur", "spurs"],
        ["wolves"] = ["wolverhampton wanderers", "wolverhampton"],
        ["brighton"] = ["brighton hove albion"],
        ["west ham"] = ["west ham united"],
        ["newcastle"] = ["newcastle united"],
        ["nottingham"] = ["nottingham forest"],
        ["atletico madrid"] = ["atletico de madrid"],
        ["real sociedad"] = ["sociedad"],
        ["dortmund"] = ["borussia dortmund"],
        ["leverkusen"] = ["bayer leverkusen"],
        ["gladbach"] = ["borussia monchengladbach"],
        ["kopenhamn"] = ["copenhagen", "fc copenhagen"],
        ["aek aten"] = ["aek athens"],
        ["olympiakos"] = ["olympiacos"],
        ["malmo"] = ["malmo ff"],
        ["goteborg"] = ["ifk goteborg", "gothenburg"],
        ["bodo glimt"] = ["bodoglimt", "bodo"],
        ["sverige"] = ["sweden"],
        ["norge"] = ["norway"],
        ["danmark"] = ["denmark"],
        ["tyskland"] = ["germany"],
        ["spanien"] = ["spain"],
        ["frankrike"] = ["france"],
        ["italien"] = ["italy"],
        ["holland"] = ["netherlands"],
        ["nederlanderna"] = ["netherlands"],
        ["schweiz"] = ["switzerland"],
        ["osterrike"] = ["austria"],
        ["belgien"] = ["belgium"],
        ["kroatien"] = ["croatia"],
        ["grekland"] = ["greece"],
        ["turkiet"] = ["turkey", "turkiye"]
    };

    /// <summary>
    /// Fetches the current live match phase and score for the given sport event if covered by a public scoreboard feed.
    /// </summary>
    public async Task<MatchLiveStatus?> FetchStatusAsync(SportEvent evt, CancellationToken ct = default)
    {
        if (evt is null || IsYouthOrNonCoveredCompetition(evt))
        {
            return null;
        }

        var (teamA, teamB) = ExtractMatchupTeams(evt);
        if (string.IsNullOrWhiteSpace(teamA) || string.IsNullOrWhiteSpace(teamB))
        {
            return null;
        }

        var endpoints = ResolveCandidateEndpoints(evt);
        foreach (var slug in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            var rawJson = await GetScoreboardJsonAsync(slug, ct);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                continue;
            }

            var match = ParseEspnScoreboardJson(rawJson, teamA, teamB);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses an ESPN scoreboard JSON payload and returns the live status for the matching fixture, if present.
    /// </summary>
    public static MatchLiveStatus? ParseEspnScoreboardJson(string rawJson, string targetTeamA, string targetTeamB)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("events", out var eventsEl) || eventsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var ev in eventsEl.EnumerateArray())
            {
                if (!ev.TryGetProperty("competitions", out var compsEl) || compsEl.ValueKind != JsonValueKind.Array || compsEl.GetArrayLength() == 0)
                {
                    continue;
                }

                var comp = compsEl[0];
                if (!comp.TryGetProperty("competitors", out var competitorsEl) || competitorsEl.ValueKind != JsonValueKind.Array || competitorsEl.GetArrayLength() < 2)
                {
                    continue;
                }

                var compNames = new List<(string DisplayName, string ShortName, string Abbrev, string Score, string HomeAway)>();
                foreach (var c in competitorsEl.EnumerateArray())
                {
                    var teamEl = c.TryGetProperty("team", out var t) ? t : default;
                    var display = teamEl.ValueKind == JsonValueKind.Object && teamEl.TryGetProperty("displayName", out var dn) ? (dn.GetString() ?? "") : "";
                    var shortName = teamEl.ValueKind == JsonValueKind.Object && teamEl.TryGetProperty("shortDisplayName", out var sn) ? (sn.GetString() ?? "") : "";
                    var abbrev = teamEl.ValueKind == JsonValueKind.Object && teamEl.TryGetProperty("abbreviation", out var ab) ? (ab.GetString() ?? "") : "";
                    var score = c.TryGetProperty("score", out var sc) ? (sc.GetString() ?? "") : "";
                    var homeAway = c.TryGetProperty("homeAway", out var ha) ? (ha.GetString() ?? "") : "";
                    compNames.Add((display, shortName, abbrev, score, homeAway));
                }

                var c0 = compNames[0];
                var c1 = compNames[1];
                var directMatch = TeamsMatch(targetTeamA, c0) && TeamsMatch(targetTeamB, c1);
                var swappedMatch = TeamsMatch(targetTeamA, c1) && TeamsMatch(targetTeamB, c0);
                if (!directMatch && !swappedMatch)
                {
                    continue;
                }

                var home = compNames.FirstOrDefault(x => x.HomeAway.Equals("home", StringComparison.OrdinalIgnoreCase));
                var away = compNames.FirstOrDefault(x => x.HomeAway.Equals("away", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(home.DisplayName)) home = c0;
                if (string.IsNullOrEmpty(away.DisplayName)) away = c1;

                var statusEl = comp.TryGetProperty("status", out var cs) ? cs : (ev.TryGetProperty("status", out var es) ? es : default);
                var (phase, clockDisplay, detailText, period) = ParseEspnStatus(statusEl);

                return new MatchLiveStatus
                {
                    Phase = phase,
                    HomeTeam = home.ShortName.Length > 0 ? home.ShortName : home.DisplayName,
                    AwayTeam = away.ShortName.Length > 0 ? away.ShortName : away.DisplayName,
                    HomeScore = home.Score,
                    AwayScore = away.Score,
                    ClockDisplay = clockDisplay,
                    DetailText = detailText,
                    Period = period,
                    Source = "ESPN",
                    CheckedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }
        catch
        {
            // Ignore malformed JSON responses
        }

        return null;
    }

    private static (MatchPlayPhase Phase, string ClockDisplay, string DetailText, int Period) ParseEspnStatus(JsonElement statusEl)
    {
        if (statusEl.ValueKind != JsonValueKind.Object)
        {
            return (MatchPlayPhase.Unknown, string.Empty, string.Empty, 0);
        }

        var displayClock = statusEl.TryGetProperty("displayClock", out var dc) ? (dc.GetString() ?? string.Empty) : string.Empty;
        var period = statusEl.TryGetProperty("period", out var p) && p.TryGetInt32(out var pi) ? pi : 0;

        var typeEl = statusEl.TryGetProperty("type", out var t) ? t : default;
        var typeName = typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("name", out var tn) ? (tn.GetString() ?? "") : "";
        var state = typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("state", out var st) ? (st.GetString() ?? "") : "";
        var completed = typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("completed", out var cm) && cm.ValueKind == JsonValueKind.True;
        var shortDetail = typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("shortDetail", out var sd) ? (sd.GetString() ?? "") : "";
        var detail = typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("detail", out var dt) ? (dt.GetString() ?? "") : "";
        var rawDetail = !string.IsNullOrWhiteSpace(shortDetail) ? shortDetail : detail;

        var phase = ClassifyEspnPhase(typeName, state, completed, rawDetail);
        var localizedDetail = phase switch
        {
            MatchPlayPhase.HalftimeOrBreak => typeName.Contains("HALFTIME", StringComparison.OrdinalIgnoreCase) || rawDetail.Contains("HT", StringComparison.OrdinalIgnoreCase) || rawDetail.Contains("Half", StringComparison.OrdinalIgnoreCase)
                ? "Halvtid"
                : "Periodpaus",
            MatchPlayPhase.Finished => "Slut",
            MatchPlayPhase.InPlay => !string.IsNullOrWhiteSpace(rawDetail) ? rawDetail : displayClock,
            _ => rawDetail
        };

        return (phase, displayClock, localizedDetail, period);
    }

    private static MatchPlayPhase ClassifyEspnPhase(string typeName, string state, bool completed, string detail)
    {
        if (completed ||
            state.Equals("post", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("FINAL", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("FULL_TIME", StringComparison.OrdinalIgnoreCase) ||
            detail.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
            detail.Equals("Final", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPlayPhase.Finished;
        }

        if (typeName.Contains("HALFTIME", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("END_PERIOD", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("INTERMISSION", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("END_OF_REGULATION", StringComparison.OrdinalIgnoreCase) ||
            detail.Equals("HT", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Halftime", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("End of", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Intermission", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPlayPhase.HalftimeOrBreak;
        }

        if (state.Equals("in", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("IN_PROGRESS", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("FIRST_HALF", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("SECOND_HALF", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("OVERTIME", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPlayPhase.InPlay;
        }

        if (state.Equals("pre", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("SCHEDULED", StringComparison.OrdinalIgnoreCase))
        {
            return MatchPlayPhase.Scheduled;
        }

        return MatchPlayPhase.Unknown;
    }

    private static bool IsYouthOrNonCoveredCompetition(SportEvent evt)
    {
        var combined = $"{evt.Title} {evt.RawSubtitle} {evt.League}";
        return YouthOrStudioRegex().IsMatch(combined);
    }

    private static (string TeamA, string TeamB) ExtractMatchupTeams(SportEvent evt)
    {
        if (evt.Teams.Count >= 2)
        {
            return (evt.Teams[0], evt.Teams[1]);
        }

        var match = MatchupSplitRegex().Match(evt.Title ?? string.Empty);
        if (match.Success)
        {
            return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
        }

        return (string.Empty, string.Empty);
    }

    private static IReadOnlyList<string> ResolveCandidateEndpoints(SportEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.League) && LeagueEndpoints.TryGetValue(evt.League.Trim(), out var leagueSlugs))
        {
            return leagueSlugs;
        }

        if (!string.IsNullOrWhiteSpace(evt.Sport) && SportFallbackEndpoints.TryGetValue(evt.Sport.Trim(), out var sportSlugs))
        {
            return sportSlugs;
        }

        return [];
    }

    private async Task<string?> GetScoreboardJsonAsync(string slug, CancellationToken ct)
    {
        var url = $"https://site.api.espn.com/apis/site/v2/sports/{slug}/scoreboard";

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_boardCache.TryGetValue(url, out var cached) &&
                (DateTimeOffset.UtcNow - cached.FetchedAtUtc).TotalSeconds < 18)
            {
                return cached.RawJson;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        try
        {
            using var resp = await SharedHttp.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);

            await _cacheLock.WaitAsync(ct);
            try
            {
                _boardCache[url] = new CachedBoard(DateTimeOffset.UtcNow, json);
            }
            finally
            {
                _cacheLock.Release();
            }

            return json;
        }
        catch
        {
            return null;
        }
    }

    private static bool TeamsMatch(string rawTarget, (string DisplayName, string ShortName, string Abbrev, string Score, string HomeAway) competitor)
    {
        var targetNorm = NormalizeTeamName(rawTarget);
        if (targetNorm.Length < 2)
        {
            return false;
        }

        var candidates = new[] { competitor.DisplayName, competitor.ShortName, competitor.Abbrev }
            .Select(NormalizeTeamName)
            .Where(s => s.Length >= 2)
            .ToArray();

        foreach (var cand in candidates)
        {
            if (NamesEquivalent(targetNorm, cand))
            {
                return true;
            }
        }

        if (TeamNameAliases.TryGetValue(targetNorm, out var aliases))
        {
            foreach (var alias in aliases)
            {
                var aliasNorm = NormalizeTeamName(alias);
                if (candidates.Any(c => NamesEquivalent(aliasNorm, c)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool NamesEquivalent(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (a.Length >= 4 && b.Length >= 4 &&
            (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    internal static string NormalizeTeamName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var formD = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            var mapped = ch switch
            {
                'ø' or 'Ø' => 'o',
                'æ' or 'Æ' => 'a',
                'ß' => 's',
                _ => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' '
            };
            sb.Append(mapped);
        }

        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t is not ("fc" or "if" or "bk" or "ik" or "sk" or "ac" or "as" or "cf" or "sc" or "hc" or "afc" or "fk" or "ff" or "ssc" or "sv" or "vfb" or "rb" or "tsg" or "rc" or "ud" or "cd" or "sd"))
            .ToArray();

        return string.Join(' ', tokens);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MatchFlowTV/1.0 (+https://www.s4m.dev/)");
        return client;
    }

    [GeneratedRegex(@"\b(youth league|u19|u20|u21|u23|junior|studion|mål?extra|eftersnack|försnack)\b", RegexOptions.IgnoreCase)]
    private static partial Regex YouthOrStudioRegex();

    [GeneratedRegex(@"^(.{2,40}?)\s+(?:-|–|—|vs\.?|mot)\s+(.{2,40}?)(?:\s*[\(|•].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MatchupSplitRegex();
}
