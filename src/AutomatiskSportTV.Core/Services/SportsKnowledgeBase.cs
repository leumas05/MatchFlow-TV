using System.Text.RegularExpressions;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Classifies raw streaming titles and descriptions into canonical Swedish sports, leagues, and teams.
/// </summary>
public static partial class SportsKnowledgeBase
{
    private sealed record LeagueRule(string Pattern, string CanonicalLeague, string CanonicalSport, bool WordBoundary = true);

    public static IReadOnlyList<string> KnownSports { get; } =
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
        "Övrigt"
    ];

    private static readonly LeagueRule[] LeagueRules =
    [
        // Fotboll
        new("uefa champions league|champions league", "Champions League", "Fotboll"),
        new("uefa europa league|europa league", "Europa League", "Fotboll"),
        new("europa conference league|conference league", "Conference League", "Fotboll"),
        new("premier league", "Premier League", "Fotboll"),
        new("allsvenskan", "Allsvenskan", "Fotboll"),
        new("superettan", "Superettan", "Fotboll"),
        new("obos damallsvenskan|damallsvenskan", "Damallsvenskan", "Fotboll"),
        new("elitettan", "Elitettan", "Fotboll"),
        new("svenska cupen", "Svenska Cupen", "Fotboll"),
        new("la liga|laliga", "La Liga", "Fotboll"),
        new("serie a", "Serie A", "Fotboll"),
        new("bundesliga", "Bundesliga", "Fotboll"),
        new("ligue 1", "Ligue 1", "Fotboll"),
        new("copa del rey", "Copa del Rey", "Fotboll"),
        new("dfb-pokal|dfb pokal", "DFB-Pokal", "Fotboll"),
        new("efl championship|championship", "Championship", "Fotboll"),
        new("nations league", "Nations League", "Fotboll"),
        new("fotbolls-vm|fotbollsvm|world cup", "Fotbolls-VM", "Fotboll"),
        new("fotbolls-em|fotbollsem|euro 202\\d", "Fotbolls-EM", "Fotboll"),
        new("women's super league|wsl", "WSL", "Fotboll"),

        // Ishockey
        new("hockeyallsvenskan", "HockeyAllsvenskan", "Ishockey"),
        new("hockeyettan", "Hockeyettan", "Ishockey"),
        new("shl", "SHL", "Ishockey"),
        new("sdhl", "SDHL", "Ishockey"),
        new("nhl", "NHL", "Ishockey"),
        new("champions hockey league|chl", "CHL", "Ishockey"),
        new("hockey-vm|ishockey-vm|iihf", "Hockey-VM", "Ishockey"),
        new("junior-vm|jvm", "JVM", "Ishockey"),
        new("4 nations", "4 Nations", "Ishockey"),

        // Motorsport
        new("formel 1|formula 1|f1", "Formel 1", "Motorsport"),
        new("formel 2|formula 2|f2", "Formel 2", "Motorsport"),
        new("motogp", "MotoGP", "Motorsport"),
        new("wrc|rally-vm|rally", "Rally / WRC", "Motorsport"),
        new("indycar", "IndyCar", "Motorsport"),
        new("nascar", "NASCAR", "Motorsport"),
        new("le mans|wec|world endurance|superbike|wsbk", "WEC / Motorsport", "Motorsport"),
        new("speedway|bauhaus-ligan", "Speedway", "Motorsport"),

        // Vintersport
        new("vinterstudion", "Vinterstudion", "Vintersport"),
        new("skidskytte|biathlon", "Skidskytte", "Vintersport"),
        new("längdskidor|längdåkning|tour de ski|vasaloppet|cross-country", "Längdskidor", "Vintersport"),
        new("alpint|slalom|storslalom|störtlopp|super-g", "Alpint", "Vintersport"),
        new("backhoppning|ski jumping", "Backhoppning", "Vintersport"),
        new("curling", "Curling", "Vintersport"),

        // Handboll
        new("handbollsligan", "Handbollsligan", "Handboll"),
        new("handbolls-vm|handbollsvm", "Handbolls-VM", "Handboll"),
        new("handbolls-em|handbollsem", "Handbolls-EM", "Handboll"),
        new("ehf champions league", "EHF Champions League", "Handboll"),

        // Tennis
        new("wimbledon", "Wimbledon", "Tennis"),
        new("roland garros|franska öppna", "Roland Garros", "Tennis"),
        new("us open", "US Open", "Tennis"),
        new("australian open", "Australian Open", "Tennis"),
        new("atp", "ATP", "Tennis"),
        new("wta", "WTA", "Tennis"),
        new("davis cup", "Davis Cup", "Tennis"),

        // Basket
        new("nba", "NBA", "Basket"),
        new("wnba", "WNBA", "Basket"),
        new("euroleague", "EuroLeague", "Basket"),
        new("sbl|svenska basketligan", "SBL", "Basket"),

        // Friidrott
        new("diamond league|bauhaus-galan", "Diamond League", "Friidrott"),
        new("finnkampen", "Finnkampen", "Friidrott"),
        new("friidrotts-vm|friidrotts-em", "Friidrott Mästerskap", "Friidrott"),

        // Golf
        new("pga tour", "PGA Tour", "Golf"),
        new("dp world tour", "DP World Tour", "Golf"),
        new("the masters|masters tournament", "The Masters", "Golf"),
        new("ryder cup", "Ryder Cup", "Golf"),
        new("solheim cup", "Solheim Cup", "Golf"),
        new("liv golf", "LIV Golf", "Golf"),

        // Kampsport
        new("ufc", "UFC", "Kampsport"),
        new("wwe|smackdown|raw|wrestlemania", "WWE", "Kampsport"),
        new("mma", "MMA", "Kampsport"),

        // Amerikansk fotboll
        new("super bowl", "Super Bowl", "Amerikansk fotboll"),
        new("nfl", "NFL", "Amerikansk fotboll"),

        // Cykling
        new("tour de france", "Tour de France", "Cykling"),
        new("giro d'italia", "Giro d'Italia", "Cykling"),
        new("vuelta", "Vuelta a España", "Cykling"),

        // Innebandy & Bandy (bl.a. Expressen Livesport)
        new("svenska superligan|ssl", "SSL", "Innebandy"),
        new("elitserien.*bandy|bandy.*elitserien", "Elitserien Bandy", "Bandy")
    ];

    private static readonly (string Keyword, string Sport)[] SportKeywords =
    [
        ("fotboll|football|soccer", "Fotboll"),
        ("ishockey|hockey", "Ishockey"),
        ("innebandy|floorball", "Innebandy"),
        ("bandy", "Bandy"),
        ("motorsport|racing|grand prix|kval|sprintrace", "Motorsport"),
        ("vintersport|skidor|skidskytte|skridsko|alpint", "Vintersport"),
        ("handboll|handball", "Handboll"),
        ("tennis", "Tennis"),
        ("basket|basketball", "Basket"),
        ("friidrott|athletics|stavhopp|maraton", "Friidrott"),
        ("golf", "Golf"),
        ("boxning|boxing|kampsport|ufc|mma|wrestling", "Kampsport"),
        ("amerikansk fotboll|american football|nfl", "Amerikansk fotboll"),
        ("cykling|cycling|cykel", "Cykling")
    ];

    private static readonly Dictionary<string, (string Sport, string? League)> TeamClues = new(StringComparer.OrdinalIgnoreCase)
    {
        // Svensk fotboll
        ["AIK"] = ("Fotboll", "Allsvenskan"),
        ["Djurgården"] = ("Fotboll", "Allsvenskan"),
        ["Hammarby"] = ("Fotboll", "Allsvenskan"),
        ["Malmö FF"] = ("Fotboll", "Allsvenskan"),
        ["IFK Göteborg"] = ("Fotboll", "Allsvenskan"),
        ["BK Häcken"] = ("Fotboll", "Allsvenskan"),
        ["IF Elfsborg"] = ("Fotboll", "Allsvenskan"),
        ["IFK Norrköping"] = ("Fotboll", "Allsvenskan"),
        ["IK Sirius"] = ("Fotboll", "Allsvenskan"),
        ["Mjällby"] = ("Fotboll", "Allsvenskan"),
        ["GAIS"] = ("Fotboll", "Allsvenskan"),

        // Europeisk fotboll
        ["Arsenal"] = ("Fotboll", "Premier League"),
        ["Liverpool"] = ("Fotboll", "Premier League"),
        ["Manchester City"] = ("Fotboll", "Premier League"),
        ["Man City"] = ("Fotboll", "Premier League"),
        ["Manchester United"] = ("Fotboll", "Premier League"),
        ["Man United"] = ("Fotboll", "Premier League"),
        ["Chelsea"] = ("Fotboll", "Premier League"),
        ["Tottenham"] = ("Fotboll", "Premier League"),
        ["Newcastle"] = ("Fotboll", "Premier League"),
        ["Aston Villa"] = ("Fotboll", "Premier League"),
        ["Real Madrid"] = ("Fotboll", "La Liga"),
        ["Barcelona"] = ("Fotboll", "La Liga"),
        ["Atlético Madrid"] = ("Fotboll", "La Liga"),
        ["Bayern München"] = ("Fotboll", "Bundesliga"),
        ["Borussia Dortmund"] = ("Fotboll", "Bundesliga"),
        ["Inter"] = ("Fotboll", "Serie A"),
        ["Juventus"] = ("Fotboll", "Serie A"),
        ["AC Milan"] = ("Fotboll", "Serie A"),
        ["PSG"] = ("Fotboll", "Ligue 1"),

        // Svensk & Internationell ishockey
        ["Tre Kronor"] = ("Ishockey", null),
        ["Frölunda"] = ("Ishockey", "SHL"),
        ["Färjestad"] = ("Ishockey", "SHL"),
        ["Skellefteå"] = ("Ishockey", "SHL"),
        ["Luleå"] = ("Ishockey", "SHL"),
        ["Brynäs"] = ("Ishockey", "SHL"),
        ["Leksand"] = ("Ishockey", "SHL"),
        ["Rögle"] = ("Ishockey", "SHL"),
        ["Växjö"] = ("Ishockey", "SHL"),
        ["Hv71"] = ("Ishockey", "SHL"),
        ["Örebro Hockey"] = ("Ishockey", "SHL"),
        ["Linköping"] = ("Ishockey", "SHL"),
        ["Timrå"] = ("Ishockey", "SHL"),
        ["Modo"] = ("Ishockey", "SHL"),
        ["Malmö Redhawks"] = ("Ishockey", "SHL")
    };

    /// <summary>
    /// Populates missing sport, league, and team fields from the event's title, subtitle, and URL.
    /// </summary>
    public static void Enrich(SportEvent evt)
    {
        var combinedText = $"{evt.Title} {evt.RawSubtitle} {evt.WatchUrl}";

        if (evt.Teams.Count == 0)
        {
            evt.Teams = ExtractTeamsFromTitle(evt.Title);
        }

        if (string.IsNullOrWhiteSpace(evt.League))
        {
            foreach (var rule in LeagueRules)
            {
                var pattern = rule.WordBoundary ? $@"\b({rule.Pattern})\b" : $"({rule.Pattern})";
                if (Regex.IsMatch(combinedText, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    evt.League = rule.CanonicalLeague;
                    if (string.IsNullOrWhiteSpace(evt.Sport) || evt.Sport == "Övrigt")
                    {
                        evt.Sport = rule.CanonicalSport;
                    }
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(evt.Sport) || evt.Sport == "Övrigt")
        {
            foreach (var (keywordPattern, canonicalSport) in SportKeywords)
            {
                if (Regex.IsMatch(combinedText, $@"\b({keywordPattern})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    evt.Sport = canonicalSport;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(evt.Sport) || evt.Sport == "Övrigt" || string.IsNullOrWhiteSpace(evt.League))
        {
            foreach (var (teamName, clue) in TeamClues)
            {
                if (Regex.IsMatch(combinedText, $@"\b{Regex.Escape(teamName)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    if (string.IsNullOrWhiteSpace(evt.Sport) || evt.Sport == "Övrigt")
                    {
                        evt.Sport = clue.Sport;
                    }
                    if (string.IsNullOrWhiteSpace(evt.League) && !string.IsNullOrWhiteSpace(clue.League))
                    {
                        evt.League = clue.League;
                    }
                    if (!evt.Teams.Any(t => t.Equals(teamName, StringComparison.OrdinalIgnoreCase)))
                    {
                        evt.Teams.Add(teamName);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(evt.Sport))
        {
            evt.Sport = "Övrigt";
        }
    }

    private static List<string> ExtractTeamsFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var cleaned = PrefixCleanupRegex().Replace(title, string.Empty).Trim();
        var match = MatchupRegex().Match(cleaned);
        if (!match.Success)
        {
            return [];
        }

        var left = CleanTeamSegment(match.Groups[1].Value);
        var right = CleanTeamSegment(match.Groups[2].Value);

        var teams = new List<string>(2);
        if (left.Length >= 2) teams.Add(left);
        if (right.Length >= 2) teams.Add(right);
        return teams;
    }

    private static string CleanTeamSegment(string raw)
    {
        var segment = raw.Trim();
        var colonIdx = segment.LastIndexOf(':');
        if (colonIdx >= 0 && colonIdx < segment.Length - 2)
        {
            segment = segment[(colonIdx + 1)..].Trim();
        }
        return segment.Trim(' ', '-', '–', '|', '•', ':');
    }

    [GeneratedRegex(@"^(LIVE|DIREKT|PÅGÅR|NU)\s*[:\-–|•]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixCleanupRegex();

    [GeneratedRegex(@"^(.{2,40}?)\s+(?:-|–|—|vs\.?|mot)\s+(.{2,40}?)(?:\s*[\(|•].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MatchupRegex();
}
