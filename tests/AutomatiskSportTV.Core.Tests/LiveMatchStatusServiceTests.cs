using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class LiveMatchStatusServiceTests
{
    private const string SampleEspnScoreboardJson = """
    {
      "events": [
        {
          "name": "Arsenal at Borussia Dortmund",
          "competitions": [
            {
              "competitors": [
                {
                  "homeAway": "home",
                  "score": "2",
                  "team": { "displayName": "Arsenal", "shortDisplayName": "Arsenal", "abbreviation": "ARS" }
                },
                {
                  "homeAway": "away",
                  "score": "1",
                  "team": { "displayName": "Borussia Dortmund", "shortDisplayName": "Dortmund", "abbreviation": "BVB" }
                }
              ],
              "status": {
                "displayClock": "45:00",
                "period": 1,
                "type": {
                  "name": "STATUS_HALFTIME",
                  "state": "in",
                  "completed": false,
                  "shortDetail": "HT",
                  "detail": "Halftime"
                }
              }
            }
          ]
        },
        {
          "name": "Manchester City vs Napoli",
          "competitions": [
            {
              "competitors": [
                {
                  "homeAway": "home",
                  "score": "3",
                  "team": { "displayName": "Manchester City", "shortDisplayName": "Man City", "abbreviation": "MCI" }
                },
                {
                  "homeAway": "away",
                  "score": "0",
                  "team": { "displayName": "Napoli", "shortDisplayName": "Napoli", "abbreviation": "NAP" }
                }
              ],
              "status": {
                "displayClock": "90:00",
                "period": 2,
                "type": {
                  "name": "STATUS_FULL_TIME",
                  "state": "post",
                  "completed": true,
                  "shortDetail": "FT",
                  "detail": "Full Time"
                }
              }
            }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void ParseEspnScoreboardJson_DetectsHalftimeAndScoreline()
    {
        var status = LiveMatchStatusService.ParseEspnScoreboardJson(SampleEspnScoreboardJson, "Arsenal", "Dortmund");

        Assert.NotNull(status);
        Assert.Equal(MatchPlayPhase.HalftimeOrBreak, status!.Phase);
        Assert.Equal("2–1", status.FormatScoreline());
        Assert.Equal("Halvtid", status.DetailText);
        Assert.Equal(1, status.Period);
    }

    [Fact]
    public void ParseEspnScoreboardJson_MatchesAliasesAndDetectsFinishedMatch()
    {
        var status = LiveMatchStatusService.ParseEspnScoreboardJson(SampleEspnScoreboardJson, "Man City", "Napoli");

        Assert.NotNull(status);
        Assert.Equal(MatchPlayPhase.Finished, status!.Phase);
        Assert.Equal("3–0", status.FormatScoreline());
        Assert.Equal("Slut", status.DetailText);
    }
}
