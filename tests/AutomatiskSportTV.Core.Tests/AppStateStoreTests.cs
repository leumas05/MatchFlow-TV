using AutomatiskSportTV.Core.Models;
using AutomatiskSportTV.Core.Services;
using Xunit;

namespace AutomatiskSportTV.Core.Tests;

public class AppStateStoreTests
{
    [Fact]
    public void LoadAndSave_RoundTripsConfigAndRepairsMissingDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"astv_test_{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var store = new AppStateStore(configPath);
            var snapshot = store.Load();

            Assert.Equal(8, snapshot.Services.Count);
            Assert.Contains(snapshot.Services, s => s.ServiceId == StreamingServiceId.Expressen);
            Assert.Contains(snapshot.Services, s => s.ServiceId == StreamingServiceId.HboMax);
            Assert.Contains("Fotboll", snapshot.RankingProfile.SportPriorities);
            Assert.True(snapshot.RankingProfile.EnableIdleScreensaver);

            snapshot.RankingProfile.FavoriteTeams.Insert(0, "Hammarby");
            snapshot.RankingProfile.ScreensaverPureBlackMode = true;
            snapshot.RankingProfile.ScreensaverDelaySeconds = 90;
            snapshot.Services.RemoveAll(s => s.ServiceId == StreamingServiceId.Netflix);
            snapshot.Services.RemoveAll(s => s.ServiceId == StreamingServiceId.Expressen);
            snapshot.Services.RemoveAll(s => s.ServiceId == StreamingServiceId.HboMax);
            snapshot.RankingProfile.ServicePriorities.Remove(StreamingServiceId.Expressen);
            snapshot.RankingProfile.ServicePriorities.Remove(StreamingServiceId.HboMax);
            store.Save(snapshot);

            var reloaded = store.Load();
            Assert.Equal("Hammarby", reloaded.RankingProfile.FavoriteTeams[0]);
            Assert.True(reloaded.RankingProfile.ScreensaverPureBlackMode);
            Assert.Equal(90, reloaded.RankingProfile.ScreensaverDelaySeconds);
            Assert.Contains(reloaded.Services, s => s.ServiceId == StreamingServiceId.Netflix);
            Assert.Contains(reloaded.Services, s => s.ServiceId == StreamingServiceId.Expressen);
            Assert.Contains(reloaded.Services, s => s.ServiceId == StreamingServiceId.HboMax);
            Assert.Contains(StreamingServiceId.Expressen, reloaded.RankingProfile.ServicePriorities);
            Assert.Contains(StreamingServiceId.HboMax, reloaded.RankingProfile.ServicePriorities);

            // Verify legacy default URLs migrate cleanly to new dedicated schedule URLs
            var tv4 = reloaded.Services.First(s => s.ServiceId == StreamingServiceId.Tv4Play);
            tv4.SportsHubUrl = "https://www.tv4play.se/sport";
            var svt = reloaded.Services.First(s => s.ServiceId == StreamingServiceId.SvtPlay);
            svt.SportsHubUrl = "https://www.svtplay.se/kategori/sport";
            store.Save(reloaded);

            var migrated = store.Load();
            var migratedTv4 = migrated.Services.First(s => s.ServiceId == StreamingServiceId.Tv4Play);
            var migratedSvt = migrated.Services.First(s => s.ServiceId == StreamingServiceId.SvtPlay);
            Assert.Equal("https://www.tv4play.se/sport/tabla?d={today}", migratedTv4.SportsHubUrl);
            Assert.Equal("https://www.tv4play.se/sport/tabla?d=2026-11-24", migratedTv4.ResolveSportsHubUrl(new DateTimeOffset(2026, 11, 24, 15, 0, 0, TimeSpan.FromHours(1))));
            Assert.Equal("https://www.svtplay.se/kategori/sport?tab=schedule&scheduleCategory=all", migratedSvt.SportsHubUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
