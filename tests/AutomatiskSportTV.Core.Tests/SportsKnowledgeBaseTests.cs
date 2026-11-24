using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class SportsKnowledgeBaseTests
{
    [Theory]
    [InlineData("LIVE: Arsenal - Chelsea", "Premier League", "Fotboll")]
    [InlineData("Frölunda - Färjestad", "SHL", "Ishockey")]
    [InlineData("Formel 1: Kval från Silverstone", "Formel 1", "Motorsport")]
    [InlineData("Vinterstudion: Skidskytte masstart", "Vinterstudion", "Vintersport")]
    [InlineData("NFL RedZone Live", "NFL", "Amerikansk fotboll")]
    [InlineData("Hockeyettan: Boden - Piteå", "Hockeyettan", "Ishockey")]
    [InlineData("SSL Herr: Storvreta - Falun", "SSL", "Innebandy")]
    [InlineData("Elitserien Bandy: Villa - Sandviken", "Elitserien Bandy", "Bandy")]
    [InlineData("Pang Junxu - Riley Powell UK Championship | Kval", "Snooker", "Snooker")]
    [InlineData("UEFA Youth League: Arsenal - Dortmund", "UEFA Youth League", "Fotboll")]
    [InlineData("Skidskytte: Världscupen — Individuell start, herrar", "Skidskytte", "Vintersport")]
    [InlineData("Bandyallsvenskan: Boltic - Kungälv", "Bandyallsvenskan", "Bandy")]
    public void Enrich_DetectsSportAndLeagueFromTitle(string title, string expectedLeague, string expectedSport)
    {
        var evt = new SportEvent
        {
            ServiceId = StreamingServiceId.Viaplay,
            Title = title
        };

        SportsKnowledgeBase.Enrich(evt);

        Assert.Equal(expectedSport, evt.Sport);
        Assert.Equal(expectedLeague, evt.League);
    }

    [Fact]
    public void Enrich_ExtractsBothTeamsFromMatchupTitle()
    {
        var evt = new SportEvent
        {
            ServiceId = StreamingServiceId.Tv4Play,
            Title = "DIREKT: AIK - Djurgården (Allsvenskan)"
        };

        SportsKnowledgeBase.Enrich(evt);

        Assert.Contains("AIK", evt.Teams);
        Assert.Contains("Djurgården", evt.Teams);
        Assert.Equal("Allsvenskan", evt.League);
        Assert.Equal("Fotboll", evt.Sport);
    }
}
