using System.Text.Json;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Result of scanning a single streaming service's sports hub page.
/// </summary>
public sealed class ServiceScanResult
{
    public StreamingServiceId ServiceId { get; init; }
    public ServiceAuthState AuthState { get; init; }
    public string PageTitle { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public List<SportEvent> Events { get; init; } = [];
}

/// <summary>
/// Parses raw JSON returned by WebView2 JavaScript scanners into typed domain models.
/// </summary>
public static class ScanResultParser
{
    private sealed class RawScanPayload
    {
        public string? service { get; set; }
        public string? url { get; set; }
        public string? pageTitle { get; set; }
        public string? authState { get; set; }
        public List<RawEventPayload>? events { get; set; }
    }

    private sealed class RawEventPayload
    {
        public string? title { get; set; }
        public string? subtitle { get; set; }
        public string? sport { get; set; }
        public string? league { get; set; }
        public string? status { get; set; }
        public string? startTimeIso { get; set; }
        public string? watchUrl { get; set; }
        public string? thumbnailUrl { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ServiceScanResult Parse(StreamingServiceId serviceId, string serviceDisplayName, string? rawWebViewScriptOutput)
    {
        if (string.IsNullOrWhiteSpace(rawWebViewScriptOutput) || rawWebViewScriptOutput == "null")
        {
            return new ServiceScanResult
            {
                ServiceId = serviceId,
                AuthState = ServiceAuthState.Error
            };
        }

        var json = rawWebViewScriptOutput.Trim();

        // WebView2 ExecuteScriptAsync returns a JSON-encoded string when JS calls JSON.stringify(...)
        if (json.StartsWith('"'))
        {
            var unwrapped = JsonSerializer.Deserialize<string>(json);
            if (string.IsNullOrWhiteSpace(unwrapped))
            {
                return new ServiceScanResult { ServiceId = serviceId, AuthState = ServiceAuthState.Error };
            }
            json = unwrapped;
        }

        var payload = JsonSerializer.Deserialize<RawScanPayload>(json, JsonOptions);
        if (payload is null)
        {
            return new ServiceScanResult { ServiceId = serviceId, AuthState = ServiceAuthState.Error };
        }

        var authState = ParseAuthState(payload.authState, serviceId);
        var events = new List<SportEvent>();

        if (payload.events is not null)
        {
            foreach (var raw in payload.events)
            {
                if (string.IsNullOrWhiteSpace(raw.title))
                {
                    continue;
                }

                var status = string.Equals(raw.status, "Upcoming", StringComparison.OrdinalIgnoreCase)
                    ? EventStatus.Upcoming
                    : EventStatus.Live;

                DateTimeOffset? startTime = null;
                if (!string.IsNullOrWhiteSpace(raw.startTimeIso) &&
                    DateTimeOffset.TryParse(raw.startTimeIso, out var parsedTime))
                {
                    startTime = parsedTime;
                }

                var watchUrl = raw.watchUrl ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(watchUrl) && !UrlSecurityGuard.IsTrustedServiceUrl(serviceId, watchUrl))
                {
                    continue;
                }

                var evt = new SportEvent
                {
                    Id = SportEvent.BuildStableId(serviceId, watchUrl, raw.title),
                    ServiceId = serviceId,
                    ServiceName = serviceDisplayName,
                    Title = raw.title.Trim(),
                    RawSubtitle = raw.subtitle?.Trim() ?? string.Empty,
                    Sport = string.IsNullOrWhiteSpace(raw.sport) ? "Övrigt" : raw.sport.Trim(),
                    League = raw.league?.Trim() ?? string.Empty,
                    Status = status,
                    StartTime = startTime,
                    WatchUrl = watchUrl,
                    ThumbnailUrl = raw.thumbnailUrl?.Trim() ?? string.Empty,
                    DetectedAtUtc = DateTimeOffset.UtcNow
                };

                SportsKnowledgeBase.Enrich(evt);
                events.Add(evt);
            }
        }

        return new ServiceScanResult
        {
            ServiceId = serviceId,
            AuthState = authState,
            PageTitle = payload.pageTitle ?? string.Empty,
            Url = payload.url ?? string.Empty,
            Events = events
        };
    }

    private static ServiceAuthState ParseAuthState(string? raw, StreamingServiceId serviceId)
    {
        if (serviceId == StreamingServiceId.SvtPlay && string.Equals(raw, "not_required", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceAuthState.NotRequired;
        }

        return raw?.ToLowerInvariant() switch
        {
            "authenticated" => ServiceAuthState.Authenticated,
            "not_required" => ServiceAuthState.NotRequired,
            "login_required" => ServiceAuthState.LoginRequired,
            "profile_picker" => ServiceAuthState.ProfileSelectionRequired,
            "rate_limited_or_challenge" => ServiceAuthState.RateLimitedOrChallenge,
            "error" => ServiceAuthState.Error,
            _ => ServiceAuthState.Unknown
        };
    }
}
