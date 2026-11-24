using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Enforces conservative, polite background scan intervals and exponential backoff
/// so streaming services are never hammered with repeated requests or auth-gate polling.
/// </summary>
public sealed class ScanPolicyManager
{
    public const int MinAutoScanIntervalSeconds = 120;
    public const int DefaultAutoScanIntervalSeconds = 300;
    public const int MaxAutoScanIntervalSeconds = 1800;

    private sealed class ServiceBackoffState
    {
        public int ConsecutiveIssues { get; set; }
        public DateTimeOffset NextAllowedUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<StreamingServiceId, ServiceBackoffState> _states = [];

    /// <summary>
    /// Determines whether a service is eligible for an automatic background scan right now.
    /// Manual user-initiated scans bypass this policy and reset backoff.
    /// </summary>
    public bool ShouldAutoScan(ServiceConfig service, DateTimeOffset nowUtc)
    {
        if (!service.Enabled)
        {
            return false;
        }

        // Never repeatedly poll services where the user hasn't signed in or picked a profile yet
        if (service.RequiresLogin &&
            service.AuthState is ServiceAuthState.LoginRequired or ServiceAuthState.ProfileSelectionRequired)
        {
            return false;
        }

        if (_states.TryGetValue(service.ServiceId, out var state) && nowUtc < state.NextAllowedUtc)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records the outcome of a scan and schedules exponential backoff on errors or challenges.
    /// </summary>
    public void RecordOutcome(StreamingServiceId serviceId, ServiceAuthState authState, DateTimeOffset nowUtc)
    {
        if (!_states.TryGetValue(serviceId, out var state))
        {
            state = new ServiceBackoffState();
            _states[serviceId] = state;
        }

        if (authState is ServiceAuthState.Authenticated or ServiceAuthState.NotRequired)
        {
            state.ConsecutiveIssues = 0;
            state.NextAllowedUtc = DateTimeOffset.MinValue;
            return;
        }

        if (authState is ServiceAuthState.RateLimitedOrChallenge or ServiceAuthState.Error)
        {
            state.ConsecutiveIssues++;
            // Exponential backoff: 15 min -> 30 min -> 60 min (capped at 60 min)
            var backoffMinutes = authState == ServiceAuthState.RateLimitedOrChallenge
                ? Math.Min(120, 30 * (1 << Math.Min(state.ConsecutiveIssues - 1, 2)))
                : Math.Min(60, 15 * (1 << Math.Min(state.ConsecutiveIssues - 1, 2)));
            state.NextAllowedUtc = nowUtc.AddMinutes(backoffMinutes);
        }
    }

    /// <summary>
    /// Clears any cooldown/backoff for a service (e.g. after the user signs in or requests a manual scan).
    /// </summary>
    public void Reset(StreamingServiceId serviceId)
    {
        _states.Remove(serviceId);
    }

    public DateTimeOffset? GetNextAllowedScanUtc(StreamingServiceId serviceId)
    {
        if (_states.TryGetValue(serviceId, out var state) && state.NextAllowedUtc > DateTimeOffset.MinValue)
        {
            return state.NextAllowedUtc;
        }
        return null;
    }

    public static int ClampScanInterval(int seconds) =>
        Math.Clamp(seconds, MinAutoScanIntervalSeconds, MaxAutoScanIntervalSeconds);
}
