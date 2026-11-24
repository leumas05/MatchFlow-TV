using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class RankingEngineTests
{
    [Fact]
    public void Rank_PrioritizesFavoriteTeamOverLeagueAndSport()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["AIK", "Arsenal"];
        profile.FavoriteLeagues = ["Champions League", "SHL"];
        profile.SportPriorities = ["Ishockey", "Fotboll"];

        var engine = new RankingEngine();
        var events = new[]
        {
            new SportEvent { Id = "1", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "Frölunda - Luleå (SHL)", Status = EventStatus.Live },
            new SportEvent { Id = "2", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Real Madrid - Bayern München (Champions League)", Status = EventStatus.Live },
            new SportEvent { Id = "3", ServiceId = StreamingServiceId.PrimeVideo, ServiceName = "Prime Video", Title = "Arsenal - Tottenham (Premier League)", Status = EventStatus.Live },
            new SportEvent { Id = "4", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "AIK - Hammarby (Allsvenskan)", Status = EventStatus.Live }
        };

        var ranked = engine.Rank(events, profile);

        Assert.Equal(["4", "3", "2", "1"], ranked.Select(r => r.Event.Id).ToArray());
    }

    [Fact]
    public void Rank_ExcludesMutedSportsAndBlockedKeywords()
    {
        var profile = RankingProfile.CreateDefault();
        profile.MutedSports = ["Golf"];
        profile.BlockedKeywords = ["repris"];

        var engine = new RankingEngine();
        var events = new[]
        {
            new SportEvent { Id = "golf", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "PGA Tour Live", Sport = "Golf", Status = EventStatus.Live },
            new SportEvent { Id = "repris", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "Allsvenskan Repris: AIK - MFF", Status = EventStatus.Live },
            new SportEvent { Id = "ok", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "Vinterstudion Live", Status = EventStatus.Live }
        };

        var ranked = engine.Rank(events, profile);

        Assert.Equal("ok", ranked[0].Event.Id);
        Assert.False(ranked[0].IsBlockedOrMuted);
        Assert.True(ranked[1].IsBlockedOrMuted);
        Assert.True(ranked[2].IsBlockedOrMuted);
    }

    [Fact]
    public void Rank_AppliesCustomPenaltyRulesWithoutHidingEvents()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteLeagues = ["Champions League", "Allsvenskan"];
        profile.PenaltyRules =
        [
            new PenaltyRule { Term = "studion", PenaltyPoints = -1500 },
            new PenaltyRule { Term = "Youth League", PenaltyPoints = 1200 } // positive input must still subtract
        ];

        var engine = new RankingEngine();
        var events = new[]
        {
            new SportEvent { Id = "studio", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "CL-studion: Galatasaray - Aston Villa", League = "Champions League", Sport = "Fotboll", Status = EventStatus.Live },
            new SportEvent { Id = "youth", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Feyenoord - Porto", League = "UEFA Youth League", Sport = "Fotboll", Status = EventStatus.Live },
            new SportEvent { Id = "match", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "AIK - Malmö FF", League = "Allsvenskan", Sport = "Fotboll", Status = EventStatus.Live }
        };

        var ranked = engine.Rank(events, profile);

        Assert.Equal(["match", "studio", "youth"], ranked.Select(r => r.Event.Id).ToArray());
        Assert.All(ranked, r => Assert.False(r.IsBlockedOrMuted));
        Assert.Contains("Minus: studion (-1500)", ranked[1].ScoreBreakdown);
        Assert.Contains("Minus: Youth League (-1200)", ranked[2].ScoreBreakdown);
    }
}
