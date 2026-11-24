using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Represents a pending automatic channel switch offered to the user via TV toast.
/// </summary>
public sealed class PendingSwitchOffer
{
    public required RankedSportEvent Candidate { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>
/// Manages continuous playback selection, history navigation, pinning, and smart auto-switching.
/// </summary>
public sealed class ChannelOrchestrator
{
    private readonly RankingEngine _rankingEngine;
    private readonly HashSet<string> _skippedEventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _endedEventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declinedSwitchOfferIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _history = new();
    private bool _manualQuietOverride;
    private bool _isHomePaused;

    public ChannelOrchestrator(RankingEngine? rankingEngine = null)
    {
        _rankingEngine = rankingEngine ?? new RankingEngine();
    }

    public IReadOnlyList<RankedSportEvent> LiveEvents { get; private set; } = [];
    public IReadOnlyList<RankedSportEvent> UpcomingEvents { get; private set; } = [];
    public RankedSportEvent? CurrentEvent { get; private set; }
    public string? PinnedEventId { get; private set; }
    public PendingSwitchOffer? PendingOffer { get; private set; }
    public bool IsCurrentPinned => CurrentEvent is not null && CurrentEvent.Event.Id == PinnedEventId;
    public bool IsHomePaused => _isHomePaused;

    public event Action<RankedSportEvent?>? ActiveChannelChanged;
    public event Action<PendingSwitchOffer?>? PendingSwitchOfferChanged;
    public event Action? CatalogUpdated;

    /// <summary>
    /// Updates the full catalog of discovered events, re-ranks them, and evaluates auto-switch and quiet-hour rules.
    /// </summary>
    public void UpdateCatalog(IEnumerable<SportEvent> allEvents, RankingProfile profile, bool quietHoursActive = false)
    {
        if (!quietHoursActive)
        {
            _manualQuietOverride = false;
        }

        var ranked = _rankingEngine.Rank(allEvents, profile, _skippedEventIds);
        LiveEvents = ranked
            .Where(r => r.Event.Status == EventStatus.Live && !r.IsBlockedOrMuted && !_endedEventIds.Contains(r.Event.Id))
            .ToList();
        UpcomingEvents = ranked
            .Where(r => r.Event.Status == EventStatus.Upcoming && !r.IsBlockedOrMuted)
            .OrderBy(r => r.Event.StartTime.HasValue ? 0 : 1)
            .ThenBy(r => r.Event.StartTime ?? DateTimeOffset.MaxValue)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => r.Event.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_isHomePaused || (quietHoursActive && !_manualQuietOverride))
        {
            if (CurrentEvent is not null)
            {
                CurrentEvent = null;
                PinnedEventId = null;
                ClearOffer();
                ActiveChannelChanged?.Invoke(null);
            }
            CatalogUpdated?.Invoke();
            return;
        }

        var nextAuto = LiveEvents.FirstOrDefault(r => !r.IsSkipped);

        // Refresh CurrentEvent metadata/score if it is still in LiveEvents
        if (CurrentEvent is not null)
        {
            var updatedCurrent = LiveEvents.FirstOrDefault(r => r.Event.Id == CurrentEvent.Event.Id);
            if (updatedCurrent is not null && !updatedCurrent.IsSkipped)
            {
                CurrentEvent = updatedCurrent;
                EvaluateHigherPriorityCandidate(profile);
            }
            else if (nextAuto is not null)
            {
                // Current event dropped off live list -> advance to best available non-skipped live match
                SwitchToInternal(nextAuto, recordHistory: false);
            }
            else
            {
                CurrentEvent = null;
                PinnedEventId = null;
                ClearOffer();
                ActiveChannelChanged?.Invoke(null);
            }
        }
        else if (nextAuto is not null)
        {
            SwitchToInternal(nextAuto, recordHistory: false);
        }

        CatalogUpdated?.Invoke();
    }

    /// <summary>
    /// Pauses live TV playback cleanly and returns to the home/standby dashboard without clearing discovered matches.
    /// </summary>
    public void GoToHome()
    {
        _isHomePaused = true;
        PinnedEventId = null;
        ClearOffer();
        if (CurrentEvent is not null)
        {
            CurrentEvent = null;
            ActiveChannelChanged?.Invoke(null);
        }
        CatalogUpdated?.Invoke();
    }

    /// <summary>
    /// Exits home/standby pause and resumes the highest-ranked live broadcast.
    /// </summary>
    public RankedSportEvent? ResumeLiveTv()
    {
        _isHomePaused = false;
        _manualQuietOverride = true;

        var target = LiveEvents.FirstOrDefault(r => !r.IsSkipped) ?? LiveEvents.FirstOrDefault();
        if (target is null)
        {
            CatalogUpdated?.Invoke();
            return null;
        }

        if (target.IsSkipped)
        {
            _skippedEventIds.Remove(target.Event.Id);
            RebuildLiveEventsSkippedFlags();
            target = LiveEvents.First(r => r.Event.Id == target.Event.Id);
        }

        SwitchToInternal(target, recordHistory: false);
        CatalogUpdated?.Invoke();
        return CurrentEvent;
    }

    /// <summary>
    /// Selects a specific event from the guide (and automatically un-skips it if it was previously skipped).
    /// </summary>
    public bool SelectEvent(string eventId)
    {
        var match = LiveEvents.FirstOrDefault(r => r.Event.Id == eventId);
        if (match is null)
        {
            return false;
        }

        _isHomePaused = false;
        _manualQuietOverride = true;
        if (_skippedEventIds.Remove(eventId))
        {
            RebuildLiveEventsSkippedFlags();
            match = LiveEvents.First(r => r.Event.Id == eventId);
        }

        SwitchToInternal(match, recordHistory: true);
        CatalogUpdated?.Invoke();
        return true;
    }

    /// <summary>
    /// Switches to the next ranked live channel.
    /// </summary>
    public RankedSportEvent? SwitchNext()
    {
        if (LiveEvents.Count == 0)
        {
            return null;
        }

        _isHomePaused = false;
        var pool = LiveEvents.Where(r => !r.IsSkipped).ToList();
        if (pool.Count == 0)
        {
            pool = [.. LiveEvents];
        }

        if (CurrentEvent is null)
        {
            SwitchToInternal(pool[0], recordHistory: false);
            return CurrentEvent;
        }

        var idx = pool.FindIndex(r => r.Event.Id == CurrentEvent.Event.Id);
        var nextIdx = idx < 0 ? 0 : (idx + 1) % pool.Count;
        if (pool.Count == 1 && idx == 0)
        {
            return CurrentEvent;
        }

        SwitchToInternal(pool[nextIdx], recordHistory: true);
        return CurrentEvent;
    }

    /// <summary>
    /// Returns to the previously watched live channel if still live, otherwise steps backward in the ranked list.
    /// </summary>
    public RankedSportEvent? SwitchPrevious()
    {
        _isHomePaused = false;

        while (_history.Count > 0)
        {
            var prevId = _history.Pop();
            var match = LiveEvents.FirstOrDefault(r => r.Event.Id == prevId);
            if (match is not null && match.Event.Id != CurrentEvent?.Event.Id)
            {
                SwitchToInternal(match, recordHistory: false);
                return CurrentEvent;
            }
        }

        var pool = LiveEvents.Where(r => !r.IsSkipped).ToList();
        if (pool.Count == 0)
        {
            pool = [.. LiveEvents];
        }

        if (pool.Count <= 1)
        {
            return CurrentEvent;
        }

        var idx = CurrentEvent is null ? -1 : pool.FindIndex(r => r.Event.Id == CurrentEvent.Event.Id);
        var prevIdx = idx <= 0 ? pool.Count - 1 : idx - 1;
        SwitchToInternal(pool[prevIdx], recordHistory: false);
        return CurrentEvent;
    }

    /// <summary>
    /// Skips the current broadcast from automatic playback while keeping it visible in the Sports Guide.
    /// </summary>
    public RankedSportEvent? SkipCurrent()
    {
        if (CurrentEvent is null)
        {
            return SwitchNext();
        }

        var skippedId = CurrentEvent.Event.Id;
        _skippedEventIds.Add(skippedId);
        if (PinnedEventId == skippedId)
        {
            PinnedEventId = null;
        }

        RebuildLiveEventsSkippedFlags();
        var next = LiveEvents.FirstOrDefault(r => !r.IsSkipped);
        if (next is not null)
        {
            SwitchToInternal(next, recordHistory: false);
        }
        else
        {
            CurrentEvent = null;
            ClearOffer();
            ActiveChannelChanged?.Invoke(null);
        }

        CatalogUpdated?.Invoke();
        return CurrentEvent;
    }

    /// <summary>
    /// Pins or unpins the current broadcast so automatic switching does not interrupt it.
    /// </summary>
    public bool TogglePinCurrent()
    {
        if (CurrentEvent is null)
        {
            return false;
        }

        PinnedEventId = PinnedEventId == CurrentEvent.Event.Id ? null : CurrentEvent.Event.Id;
        if (PinnedEventId is not null)
        {
            ClearOffer();
        }
        return PinnedEventId is not null;
    }

    /// <summary>
    /// Marks the currently playing event as ended and automatically picks the next highest-ranked live stream.
    /// </summary>
    public RankedSportEvent? ReportCurrentPlaybackEnded()
    {
        if (CurrentEvent is null)
        {
            return null;
        }

        var endedId = CurrentEvent.Event.Id;
        _endedEventIds.Add(endedId);
        if (PinnedEventId == endedId)
        {
            PinnedEventId = null;
        }

        LiveEvents = LiveEvents.Where(r => r.Event.Id != endedId).ToList();
        var next = LiveEvents.FirstOrDefault(r => !r.IsSkipped);
        if (next is not null)
        {
            SwitchToInternal(next, recordHistory: false);
        }
        else
        {
            CurrentEvent = null;
            ClearOffer();
            ActiveChannelChanged?.Invoke(null);
        }

        CatalogUpdated?.Invoke();
        return CurrentEvent;
    }

    public void AcceptPendingSwitch()
    {
        if (PendingOffer is null)
        {
            return;
        }

        var target = PendingOffer.Candidate;
        ClearOffer();
        SwitchToInternal(target, recordHistory: true);
    }

    public void DismissPendingSwitch()
    {
        if (PendingOffer is not null)
        {
            _declinedSwitchOfferIds.Add(PendingOffer.Candidate.Event.Id);
            ClearOffer();
        }
    }

    public void ResetSkippedEvents()
    {
        _skippedEventIds.Clear();
        _declinedSwitchOfferIds.Clear();
        RebuildLiveEventsSkippedFlags();
        CatalogUpdated?.Invoke();
    }

    private void RebuildLiveEventsSkippedFlags()
    {
        LiveEvents = LiveEvents
            .Select(r => new RankedSportEvent
            {
                Event = r.Event,
                Score = r.Score,
                IsBlockedOrMuted = r.IsBlockedOrMuted,
                IsSkipped = _skippedEventIds.Contains(r.Event.Id),
                ScoreBreakdown = r.ScoreBreakdown,
                MatchedReasons = r.MatchedReasons
            })
            .OrderBy(r => r.IsSkipped)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => r.Event.StartTime ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Event.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EvaluateHigherPriorityCandidate(RankingProfile profile)
    {
        if (CurrentEvent is null || LiveEvents.Count == 0 || IsCurrentPinned)
        {
            ClearOffer();
            return;
        }

        var top = LiveEvents.FirstOrDefault(r => !r.IsSkipped);
        if (top is null || top.Event.Id == CurrentEvent.Event.Id || top.Score <= CurrentEvent.Score + 50)
        {
            ClearOffer();
            return;
        }

        switch (profile.AutoSwitchMode)
        {
            case AutoSwitchMode.OnlyWhenCurrentEnds:
                ClearOffer();
                break;

            case AutoSwitchMode.ImmediateAutoSwitch:
                ClearOffer();
                SwitchToInternal(top, recordHistory: true);
                break;

            case AutoSwitchMode.SmartToastWithCountdown:
                if (_declinedSwitchOfferIds.Contains(top.Event.Id))
                {
                    return;
                }
                if (PendingOffer?.Candidate.Event.Id == top.Event.Id)
                {
                    return;
                }

                PendingOffer = new PendingSwitchOffer
                {
                    Candidate = top,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(profile.AutoSwitchCountdownSeconds, 5, 60))
                };
                PendingSwitchOfferChanged?.Invoke(PendingOffer);
                break;
        }
    }

    private void SwitchToInternal(RankedSportEvent target, bool recordHistory)
    {
        if (recordHistory && CurrentEvent is not null && CurrentEvent.Event.Id != target.Event.Id)
        {
            _history.Push(CurrentEvent.Event.Id);
        }

        if (PinnedEventId is not null && PinnedEventId != target.Event.Id)
        {
            PinnedEventId = null;
        }

        ClearOffer();
        CurrentEvent = target;
        ActiveChannelChanged?.Invoke(target);
    }

    private void ClearOffer()
    {
        if (PendingOffer is not null)
        {
            PendingOffer = null;
            PendingSwitchOfferChanged?.Invoke(null);
        }
    }

    private int FindCurrentIndex()
    {
        if (CurrentEvent is null)
        {
            return -1;
        }

        for (var i = 0; i < LiveEvents.Count; i++)
        {
            if (LiveEvents[i].Event.Id == CurrentEvent.Event.Id)
            {
                return i;
            }
        }
        return -1;
    }
}
