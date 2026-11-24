using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class SecurityAndPolicyTests
{
    [Theory]
    [InlineData(StreamingServiceId.SvtPlay, "https://www.svtplay.se/video/abc", true)]
    [InlineData(StreamingServiceId.Expressen, "https://livesport.expressen.se/sv/live", true)]
    [InlineData(StreamingServiceId.Expressen, "https://api.staylive.tv/v1/stream", true)]
    [InlineData(StreamingServiceId.DisneyPlus, "https://www.disneyplus.com/sv-se/browse/page-1eaf1f02-deb4-48d0-b54f-cbf7a843e8f3", true)]
    [InlineData(StreamingServiceId.HboMax, "https://play.hbomax.com/sport/event/123", true)]
    [InlineData(StreamingServiceId.HboMax, "https://play.max.com/sport/eurosport-1", true)]
    [InlineData(StreamingServiceId.HboMax, "https://fake-hbomax.example/sport", false)]
    [InlineData(StreamingServiceId.Viaplay, "http://viaplay.se/sport", false)]
    [InlineData(StreamingServiceId.Netflix, "https://evil-phishing.example/netflix.com/watch", false)]
    [InlineData(StreamingServiceId.Tv4Play, "javascript:alert(1)", false)]
    [InlineData(StreamingServiceId.PrimeVideo, "https://127.0.0.1/admin", false)]
    [InlineData(StreamingServiceId.DisneyPlus, "file:///C:/Windows/System32", false)]
    public void UrlSecurityGuard_EnforcesHttpsAndTrustedDomains(StreamingServiceId service, string url, bool expected)
    {
        Assert.Equal(expected, UrlSecurityGuard.IsTrustedServiceUrl(service, url));
    }

    [Fact]
    public void UrlSecurityGuard_SanitizesQueryTokensFromDisplayUrls()
    {
        var raw = "https://viaplay.se/sport/fotboll/match?token=secret123&session=xyz#frag";
        Assert.Equal("https://viaplay.se/sport/fotboll/match", UrlSecurityGuard.SanitizeForDisplay(raw));
    }

    [Fact]
    public void ScanPolicyManager_SkipsUnauthenticatedServicesAndAppliesBackoffOnChallenges()
    {
        var policy = new ScanPolicyManager();
        var now = DateTimeOffset.UtcNow;

        var needsLogin = new ServiceConfig
        {
            ServiceId = StreamingServiceId.Viaplay,
            Enabled = true,
            RequiresLogin = true,
            AuthState = ServiceAuthState.LoginRequired
        };
        Assert.False(policy.ShouldAutoScan(needsLogin, now));

        var authed = new ServiceConfig
        {
            ServiceId = StreamingServiceId.Tv4Play,
            Enabled = true,
            RequiresLogin = true,
            AuthState = ServiceAuthState.Authenticated
        };
        Assert.True(policy.ShouldAutoScan(authed, now));

        policy.RecordOutcome(StreamingServiceId.Tv4Play, ServiceAuthState.RateLimitedOrChallenge, now);
        Assert.False(policy.ShouldAutoScan(authed, now.AddMinutes(10)));
        Assert.True(policy.ShouldAutoScan(authed, now.AddMinutes(31)));

        policy.Reset(StreamingServiceId.Tv4Play);
        Assert.True(policy.ShouldAutoScan(authed, now));
    }
}
