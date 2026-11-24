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
        var now = DateTimeOffset.Now;
        var events = new[]
        {
            new SportEvent { Id = "thu-swe", ServiceId = StreamingServiceId.SvtPlay, ServiceName = "SVT Play", Title = "Handboll: Sverige - Island", StartTime = now.AddDays(2).AddHours(1), Status = EventStatus.Upcoming },
            new SportEvent { Id = "today-como", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Como - AEK Aten", League = "Champions League", Sport = "Fotboll", StartTime = now.AddHours(3), Status = EventStatus.Upcoming },
            new SportEvent { Id = "today-ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", League = "Champions League", Sport = "Fotboll", StartTime = now.AddHours(3), Status = EventStatus.Upcoming },
            new SportEvent { Id = "today-early", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "CL-studion: Galatasaray - Aston Villa", League = "Champions League", Sport = "Fotboll", StartTime = now.AddHours(1), Status = EventStatus.Upcoming },
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

    [Fact]
    public void ReportMatchStatus_HalftimeOffers30sSwitchThenReturnsImmediatelyWhenPlayResumes()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];
        profile.EnableSmartBreakSwitching = true;
        profile.BreakSwitchDelaySeconds = 30;

        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", League = "Champions League", Status = EventStatus.Live },
            new SportEvent { Id = "city", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Man City - Napoli", League = "Champions League", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);
        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);

        // Halftime triggers a ~30s switch offer to Man City - Napoli
        orchestrator.ReportMatchStatus("ars", new MatchLiveStatus
        {
            Phase = MatchPlayPhase.HalftimeOrBreak,
            HomeScore = "2",
            AwayScore = "1",
            DetailText = "Halvtid",
            Period = 1
        }, profile);

        Assert.NotNull(orchestrator.PendingOffer);
        Assert.Equal(SwitchOfferKind.HalftimeOrBreak, orchestrator.PendingOffer!.Kind);
        Assert.Equal("city", orchestrator.PendingOffer.Candidate.Event.Id);

        // Accepting (or 30s countdown expiring) temporarily switches to city and tracks ars as PausedPrimaryEvent
        orchestrator.AcceptPendingSwitch();
        Assert.Equal("city", orchestrator.CurrentEvent?.Event.Id);
        Assert.Equal("ars", orchestrator.PausedPrimaryEvent?.Event.Id);

        // Second half starts on Arsenal -> immediately switches RIGHT BACK to Arsenal!
        string? returnedId = null;
        orchestrator.ReturnedFromBreak += (primary, _) => returnedId = primary.Event.Id;

        orchestrator.ReportMatchStatus("ars", new MatchLiveStatus
        {
            Phase = MatchPlayPhase.InPlay,
            HomeScore = "2",
            AwayScore = "1",
            ClockDisplay = "46'",
            Period = 2
        }, profile);

        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);
        Assert.Equal("ars", returnedId);
        Assert.Null(orchestrator.PausedPrimaryEvent);
    }

    [Fact]
    public void ReportMatchStatus_DismissingHalftimeOfferLetsUserStayForThatPeriod()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];
        profile.EnableSmartBreakSwitching = true;

        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", Status = EventStatus.Live },
            new SportEvent { Id = "city", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Man City - Napoli", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);

        var htStatus = new MatchLiveStatus { Phase = MatchPlayPhase.HalftimeOrBreak, DetailText = "Halvtid", Period = 1 };
        orchestrator.ReportMatchStatus("ars", htStatus, profile);
        Assert.NotNull(orchestrator.PendingOffer);

        // User chooses "Stanna kvar"
        orchestrator.DismissPendingSwitch();
        Assert.Null(orchestrator.PendingOffer);
        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);

        // Subsequent poll during the same Period 1 break does NOT re-open the toast
        orchestrator.ReportMatchStatus("ars", htStatus, profile);
        Assert.Null(orchestrator.PendingOffer);
    }

    [Fact]
    public void ReportMatchStatus_FinishedOffersSwitchAndDoesNotReturn()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];
        profile.EnableSmartBreakSwitching = true;

        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", Status = EventStatus.Live },
            new SportEvent { Id = "city", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Man City - Napoli", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);

        orchestrator.ReportMatchStatus("ars", new MatchLiveStatus { Phase = MatchPlayPhase.Finished, HomeScore = "3", AwayScore = "1", DetailText = "Slut", Period = 2 }, profile);
        Assert.Equal(SwitchOfferKind.MatchFinished, orchestrator.PendingOffer?.Kind);

        orchestrator.AcceptPendingSwitch();
        Assert.Equal("city", orchestrator.CurrentEvent?.Event.Id);
        Assert.Null(orchestrator.PausedPrimaryEvent);
    }

    [Fact]
    public void UpdateCatalog_NeverAutoEntersFinishedOrHalftimeMatchWhenActiveMatchExists()
    {
        var profile = RankingProfile.CreateDefault();
        profile.FavoriteTeams = ["Arsenal"];

        var orchestrator = new ChannelOrchestrator();
        orchestrator.PreloadMatchStatuses(new Dictionary<string, MatchLiveStatus>
        {
            ["ars"] = new() { Phase = MatchPlayPhase.Finished, HomeScore = "2", AwayScore = "1", DetailText = "Slut" },
            ["city"] = new() { Phase = MatchPlayPhase.HalftimeOrBreak, HomeScore = "1", AwayScore = "0", DetailText = "Halvtid", Period = 1 },
            ["bask"] = new() { Phase = MatchPlayPhase.InPlay, HomeScore = "42", AwayScore = "38", ClockDisplay = "Q2 05:10", Period = 2 }
        });

        var events = new[]
        {
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", League = "Champions League", Status = EventStatus.Live },
            new SportEvent { Id = "city", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Man City - Napoli", League = "Champions League", Status = EventStatus.Live },
            new SportEvent { Id = "bask", ServiceId = StreamingServiceId.DisneyPlus, ServiceName = "Disney+", Title = "Creighton vs. Michigan", Sport = "Basket", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);

        // Must skip Finished (Arsenal) & Halftime (Man City) and auto-enter actively playing basketball!
        Assert.Equal("bask", orchestrator.CurrentEvent?.Event.Id);
        // All 3 remain visible in LiveEvents sorted: InPlay -> Halftime -> Finished
        Assert.Equal(["bask", "city", "ars"], orchestrator.LiveEvents.Select(e => e.Event.Id).ToArray());

        // User can still voluntarily click the Finished match in Sportguiden to watch post-match show
        Assert.True(orchestrator.SelectEvent("ars"));
        Assert.Equal("ars", orchestrator.CurrentEvent?.Event.Id);
    }

    [Fact]
    public void UpdateCatalog_DoesNotAutoPlayWhenOnlyFinishedMatchesExist()
    {
        var profile = RankingProfile.CreateDefault();
        var orchestrator = new ChannelOrchestrator();
        orchestrator.PreloadMatchStatuses(new Dictionary<string, MatchLiveStatus>
        {
            ["ars"] = new() { Phase = MatchPlayPhase.Finished, HomeScore = "2", AwayScore = "1", DetailText = "Slut" }
        });

        var events = new[]
        {
            new SportEvent { Id = "ars", ServiceId = StreamingServiceId.Viaplay, ServiceName = "Viaplay", Title = "Arsenal - Dortmund", League = "Champions League", Status = EventStatus.Live }
        };

        orchestrator.UpdateCatalog(events, profile);

        Assert.Null(orchestrator.CurrentEvent);
        Assert.Single(orchestrator.LiveEvents);
    }

    [Fact]
    public void UpdateCatalog_ExcludesPastUpcomingEvents()
    {
        var profile = RankingProfile.CreateDefault();
        var orchestrator = new ChannelOrchestrator();
        var events = new[]
        {
            new SportEvent { Id = "past", ServiceId = StreamingServiceId.Expressen, ServiceName = "Expressen", Title = "Edsbyn - Villa", StartTime = DateTimeOffset.Now.AddHours(-4), Status = EventStatus.Upcoming },
            new SportEvent { Id = "future", ServiceId = StreamingServiceId.DisneyPlus, ServiceName = "Disney+", Title = "Creighton vs. Michigan", StartTime = DateTimeOffset.Now.AddHours(1), Status = EventStatus.Upcoming }
        };

        orchestrator.UpdateCatalog(events, profile);

        Assert.Single(orchestrator.UpcomingEvents);
        Assert.Equal("future", orchestrator.UpcomingEvents[0].Event.Id);
    }
}
