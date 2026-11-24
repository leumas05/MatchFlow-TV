using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class ChannelOrchestratorTests
{
    [Fact]
    public void UpdateCatalog_AutoSelectsTopRankedLiveMatchAndAdvancesWhenEnded()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];

        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "shl", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "Frölunda - Färjestad (SHL)", Status = EventStatus.Live },
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.PrimeVideo, ServiceName = "Prime Video", Title = "Arsenal - Liverpool", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);

        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);

        var next = orchestrator.ReportCurrentPlaybackEnded();
        Assert.Equal("shl", next?.Event.Id);
    }

    [Fact]
    public void PinCurrent_PreventsImmediateAutoSwitchWhenHigherPriorityStarts()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];
        profile.AutoSwitchMode = AutoSwitchMode.ImmediateAutoSwitch;

        var orchestrator = new ChannelOrchestrator();
        var initial = new[]
        {
            new SportEvent { Id = "shl", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "Frölunda - Färjestad (SHL)", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(initial, profile);
        Assert.True(orchestrator.TogglePinCurrent());

        var withHigher = initial.Append(new SportEvent
        {
            Id = "ars",
            ServiceId = StreamingServiceId.PrimeVideo,
            ServiceName = "Prime Video",
            Title = "Arsenal - Chelsea",
            Status = EventStatus.Live
        });

        orchestrator.UpdateCatalog(withHigher, profile);

        Assert.Equal("shl", orchestrator.CurrentEvent?.Event.Id);
        Assert.Null(orchestrator.PendingOffer);
    }

    [Fact]
    public void SwitchNextAndPrevious_CyclesAndUsesHistory()
    {
        var profile = RankingProfile.CreateDefault();
        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "a", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "Fotboll: Allsvenskan", Status = EventStatus.Live },
            new SportEvent { Id = "b", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Ishockey: NHL", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);
        var firstId = orchestrator.CurrentEvent?.Event.Id;

        orchestrator.SwitchNext();
        var secondId = orchestrator.CurrentEvent?.Event.Id;
        Assert.NotEqual(firstId, secondId);

        orchestrator.SwitchPrevious();
        Assert.Equal(firstId, orchestrator.CurrentEvent?.Event.Id);
    }

    [Fact]
    public void UpdateCatalog_SortsUpcomingChronologicallyAndBreaksSameTimeTiesByScore()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Sverige", "Arsenal"];
        profile.FavoriteLeagues = ["Champions League"];

        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "thu-swe", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "Handboll: Sverige - Island", StartTime = new DateTimeOffset(2026, 11, 26, 18, 5, 0, TimeSpan.FromHours(1)), Status = EventStatus.Upcoming },
            new SportEvent { Id = "today-como", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Como - AEK Aten", League = "Champions League", Sport = "Fotboll", StartTime = new DateTimeOffset(2026, 11, 24, 20, 50, 0, TimeSpan.FromHours(1)), Status = EventStatus.Upcoming },
            new SportEvent { Id = "today-ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", League = "Champions League", Sport = "Fotboll", StartTime = new DateTimeOffset(2026, 11, 24, 20, 50, 0, TimeSpan.FromHours(1)), Status = EventStatus.Upcoming },
            new SportEvent { Id = "today-early", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "CL-studion: Galatasaray - Aston Villa", League = "Champions League", Sport = "Fotboll", StartTime = new DateTimeOffset(2026, 11, 24, 18, 30, 0, TimeSpan.FromHours(1)), Status = EventStatus.Upcoming },
            new SportEvent { Id = "no-time", ServiceId = StreamingServiceId.DisneyPlus, ServiceName = "Disney+", Title = "Belmont vs. Akron", StartTime = null, Status = EventStatus.Upcoming }
        };

        orchestrator.UpdateCatalog(events, profile);

        Assert.Equal(
            ["today-early", "today-ars", "today-como", "thu-swe", "no-time"],
            orchestrator.UpcomingEvents.Select(u => u.Event.Id).ToArray());
    }

    [Fact]
    public void SkipCurrent_KeepsMatchVisibleInLiveEventsAndSelectEventResumesIt()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];

        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", Status = EventStatus.Live },
            new SportEvent { Id = "shl", ServiceId = StreamingServiceId.Tv4Play, ServiceName = "TV4 Play", Title = "Luleå HF - Malmö Redhawks (SHL)", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);
        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);

        orchestrator.SkipCurrent();
        Assert.Equal("shl", orchestrator.CurrentEvent?.Event.Id);
        // Skipped event MUST remain visible in LiveEvents (Sportguiden) marked IsSkipped=true
        Assert.Equal(2, orchestrator.LiveEvents.Count);
        var skippedEntry = Assert.Single(orchestrator.LiveEvents, e => e.Event.Id == "ars");
        Assert.True(skippedEntry.IsSkipped);

        // Selecting it manually in Sportguiden un-skips and plays it again
        Assert.True(orchestrator.SelectEvent("ars"));
        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);
        Assert.False(orchestrator.LiveEvents.Single(e => e.Event.Id == "ars").IsSkipped);
    }

    [Fact]
    public void GoToHome_PausesPlaybackUntilResumeLiveTvIsCalled()
    {
        var profile = RankingProfile.CreateDefault();
        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "live1", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "Fotboll: Allsvenskan", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);
        Assert.Equal("live1", orchestrator.CurrentEvent?.Event.Id);

        orchestrator.GoToHome();
        Assert.True(orchestrator.IsHomePaused);
        Assert.Null(orchestrator.CurrentEvent);
        Assert.Single(orchestrator.LiveEvents);

        // Background scan while on Home screen must NOT hijack the user back to video
        orchestrator.UpdateCatalog(events, profile);
        Assert.Null(orchestrator.CurrentEvent);

        orchestrator.ResumeLiveTv();
        Assert.False(orchestrator.IsHomePaused);
        Assert.Equal("live1", orchestrator.CurrentEvent?.Event.Id);
    }
}
