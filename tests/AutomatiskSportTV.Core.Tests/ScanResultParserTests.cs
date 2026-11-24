using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class ScanResultParserTests
{
    [Fact]
    public void Parse_HandlesDoubleEncodedWebViewScriptJson()
    {
        const string innerJson = """
        {
          "service": "SvtPlay",
          "url": "https://www.svtplay.se/kategori/sport",
          "pageTitle": "Sport | SVT Play",
          "authState": "not_required",
          "events": [
            {
              "title": "Vinterstudion: Skidskytte damer",
              "subtitle": "Direktsändning från Östersund",
              "status": "Live",
              "watchUrl": "https://www.svtplay.se/video/abc123"
            },
            {
              "title": "Handboll: Sverige - Norge",
              "subtitle": "Börjar 19:00",
              "status": "Upcoming",
              "startTimeIso": "2026-11-24T19:00:00+01:00",
              "watchUrl": "https://www.svtplay.se/video/def456"
            }
          ]
        }
        """;

        var doubleEncoded = System.Text.Json.JsonSerializer.Serialize(innerJson);
        var result = ScanResultParser.Parse(StreamingServiceId.SvtPlay, "SVT Play", doubleEncoded);

        Assert.Equal(ServiceAuthState.NotRequired, result.AuthState);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(EventStatus.Live, result.Events[0].Status);
        Assert.Equal("Vintersport", result.Events[0].Sport);
        Assert.Equal(EventStatus.Upcoming, result.Events[1].Status);
        Assert.Equal("Handboll", result.Events[1].Sport);
    }

    [Fact]
    public void Parse_ParsesExpressenLivesportEvents()
    {
        const string json = """
        {
          "service": "Expressen",
          "url": "https://livesport.expressen.se",
          "pageTitle": "Expressen Livesport",
          "authState": "authenticated",
          "events": [
            {
              "title": "Kalmar HC - Nybro Vikings (Hockeyettan)",
              "subtitle": "Direktsändning",
              "status": "Live",
              "watchUrl": "https://livesport.expressen.se/livestream/hockeyettan-123"
            }
          ]
        }
        """;

        var result = ScanResultParser.Parse(StreamingServiceId.Expressen, "Expressen", json);

        Assert.Equal(ServiceAuthState.Authenticated, result.AuthState);
        Assert.Single(result.Events);
        Assert.Equal(StreamingServiceId.Expressen, result.Events[0].ServiceId);
        Assert.Equal("Ishockey", result.Events[0].Sport);
        Assert.Equal("Hockeyettan", result.Events[0].League);
    }

    [Fact]
    public void Parse_ParsesHboMaxEurosportEvents()
    {
        const string json = """
        {
          "service": "HboMax",
          "url": "https://play.hbomax.com/sport",
          "pageTitle": "Sport | HBO Max",
          "authState": "authenticated",
          "events": [
            {
              "title": "Roland Garros: Final herrar",
              "subtitle": "Eurosport 1 Live",
              "status": "Live",
              "watchUrl": "https://play.hbomax.com/video/watch/roland-garros-final"
            }
          ]
        }
        """;

        var result = ScanResultParser.Parse(StreamingServiceId.HboMax, "HBO Max", json);

        Assert.Equal(ServiceAuthState.Authenticated, result.AuthState);
        Assert.Single(result.Events);
        Assert.Equal(StreamingServiceId.HboMax, result.Events[0].ServiceId);
        Assert.Equal("Tennis", result.Events[0].Sport);
        Assert.Equal("Roland Garros", result.Events[0].League);
    }

    [Fact]
    public void Parse_CapturesDiagnosticsAndDisneyPlusAuthenticatedEvents()
    {
        const string json = """
        {
          "service": "DisneyPlus",
          "url": "https://www.disneyplus.com/sv-se/browse/page-1eaf1f02-deb4-48d0-b54f-cbf7a843e8f3?token=secret123",
          "pageTitle": "Sport | Disney+",
          "authState": "authenticated",
          "extractorUsed": "DisneyPlus:DOM",
          "linksInspected": 42,
          "pageSnippet": "Live och kommande UEFA Europa League",
          "events": [
            {
              "title": "Malmö FF - Galatasaray",
              "subtitle": "LIVE • UEFA Europa League",
              "status": "Live",
              "watchUrl": "https://www.disneyplus.com/sv-se/browse/entity-12345678-aaaa-bbbb-cccc-1234567890ab"
            }
          ]
        }
        """;

        var result = ScanResultParser.Parse(StreamingServiceId.DisneyPlus, "Disney+", json);

        Assert.Equal(ServiceAuthState.Authenticated, result.AuthState);
        Assert.Equal("DisneyPlus:DOM", result.ExtractorUsed);
        Assert.Equal(42, result.LinksInspected);
        Assert.Equal("https://www.disneyplus.com/sv-se/browse/page-1eaf1f02-deb4-48d0-b54f-cbf7a843e8f3", result.Url);
        Assert.Contains("UEFA Europa League", result.PageSnippet);
        Assert.Single(result.Events);
        Assert.Equal(EventStatus.Live, result.Events[0].Status);
        Assert.Equal("Fotboll", result.Events[0].Sport);
        Assert.Equal("Europa League", result.Events[0].League);
    }
}
