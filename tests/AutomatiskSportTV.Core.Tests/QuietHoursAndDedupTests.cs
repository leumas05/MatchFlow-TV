using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class QuietHoursAndDedupTests
{
    [Theory]
    [InlineData("23:30", "07:00", 23, 45, true)]
    [InlineData("23:30", "07:00", 3, 15, true)]
    [InlineData("23:30", "07:00", 7, 0, false)]
    [InlineData("23:30", "07:00", 14, 0, false)]
    [InlineData("09:00", "17:00", 12, 0, true)]
    [InlineData("09:00", "17:00", 18, 0, false)]
    public void QuietHoursEvaluator_HandlesOvernightAndDaytimeWindows(
        string start, string end, int hour, int minute, bool expectedQuiet)
    {
        var profile = RankingProfile.CreateDefault();
        profile.EnableQuietHours = true;
        profile.QuietHoursStart = start;
        profile.QuietHoursEnd = end;

        Assert.Equal(expectedQuiet, QuietHoursEvaluator.IsQuietNow(profile, new TimeOnly(hour, minute)));
    }

    [Fact]
    public void EventDeduplicator_KeepsHighestPriorityServiceAndNotesAlternative()
    {
        var profile = RankingProfile.CreateDefault();
        profile.ServicePriorities = [StreamingServiceId.SvtPlay, StreamingServiceId.Viaplay];
        profile.DeduplicateSameMatch = true;

        var engine = new RankingEngine();
        var events = new[]
        {
            new SportEvent { Id = "svt", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "LIVE: AIK - Djurgården", Status = EventStatus.Live },
            new SportEvent { Id = "via", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "AIK - Djurgården (HD)", Status = EventStatus.Live },
            new SportEvent { Id = "shl", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "Frölunda - Färjestad", Status = EventStatus.Live }
        };

        var ranked = engine.Rank(events, profile);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("svt", ranked[0].Event.Id);
        Assert.Contains("även på Viaplay", ranked[0].ScoreBreakdown);
    }

    [Fact]
    public void ChannelOrchestrator_SuppressesAutoPlayDuringQuietHoursUntilManualPick()
    {
        var profile = RankingProfile.CreateDefault();
        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "nhl", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "NHL: Toronto - Boston", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile, quietHoursActive: true);
        Assert.Null(orchestrator.CurrentEvent);
        Assert.Single(orchestrator.LiveEvents);

        // User intentionally selects the match during quiet hours -> manual override honors their pick
        Assert.True(orchestrator.SelectEvent("nhl"));
        Assert.Equal("nhl", orchestrator.CurrentEvent?.Event.Id);

        orchestrator.UpdateCatalog(events, profile, quietHoursActive: true);
        Assert.Equal("nhl", orchestrator.CurrentEvent?.Event.Id);
    }
}
