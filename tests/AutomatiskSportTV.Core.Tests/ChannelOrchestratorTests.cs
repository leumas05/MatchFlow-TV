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
}
