using System.Text.Json;
using System.Text.Json.Serialization;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.Core.Services;

/// <summary>
/// Persisted application configuration snapshot.
/// </summary>
public sealed class AppConfigSnapshot
{
    public RankingProfile RankingProfile { get; set; } = RankingProfile.CreateDefault();
    public List<ServiceConfig> Services { get; set; } = ServiceConfig.CreateDefaults();
}

/// <summary>
/// Loads and persists user ranking preferences and streaming service configurations as JSON.
/// </summary>
public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configFilePath;
    private readonly string _backupFilePath;
    private readonly object _ioLock = new();

    public AppStateStore(string? configFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(configFilePath))
        {
            _configFilePath = configFilePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _configFilePath = Path.Combine(appData, "AutomatiskSportTV", "config.json");
        }
        _backupFilePath = $"{_configFilePath}.bak";
    }

    public string ConfigFilePath => _configFilePath;

    public AppConfigSnapshot Load()
    {
        lock (_ioLock)
        {
            if (!File.Exists(_configFilePath))
            {
                if (TryDeserializeFile(_backupFilePath, out var recoveredFromBackup))
                {
                    return recoveredFromBackup;
                }

                var defaults = new AppConfigSnapshot();
                Save(defaults);
                return defaults;
            }

            if (TryDeserializeFile(_configFilePath, out var snapshot))
            {
                return snapshot;
            }

            if (TryDeserializeFile(_backupFilePath, out var backupSnapshot))
            {
                return backupSnapshot;
            }

            return new AppConfigSnapshot();
        }
    }

    private static bool TryDeserializeFile(string path, out AppConfigSnapshot snapshot)
    {
        try
        {
            if (!File.Exists(path))
            {
                snapshot = new AppConfigSnapshot();
                return false;
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AppConfigSnapshot>(json, JsonOptions);
            if (parsed is null)
            {
                snapshot = new AppConfigSnapshot();
                return false;
            }

            parsed.RankingProfile ??= RankingProfile.CreateDefault();
            parsed.Services ??= ServiceConfig.CreateDefaults();
            EnsureAllServicesPresent(parsed);
            EnsureDefaultSportsPresent(parsed.RankingProfile);
            snapshot = parsed;
            return true;
        }
        catch
        {
            snapshot = new AppConfigSnapshot();
            return false;
        }
    }

    public void Save(AppConfigSnapshot snapshot)
    {
        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{_configFilePath}.{Environment.ProcessId}.tmp";
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_configFilePath))
            {
                try
                {
                    File.Copy(_configFilePath, _backupFilePath, overwrite: true);
                }
                catch
                {
                    // Non-fatal if backup copy is briefly locked
                }
            }

            File.Move(tempPath, _configFilePath, overwrite: true);
        }
    }

    private static readonly Dictionary<StreamingServiceId, string[]> LegacyDefaultUrls = new()
    {
        [StreamingServiceId.SvtPlay] = ["https://www.svtplay.se/kategori/sport"],
        [StreamingServiceId.Tv4Play] = ["https://www.tv4play.se/sport"],
        [StreamingServiceId.Viaplay] = ["https://viaplay.se/sport"],
        [StreamingServiceId.DisneyPlus] = ["https://www.disneyplus.com/home", "https://www.disneyplus.com/sv-se/browse/sport"],
        [StreamingServiceId.Expressen] = ["https://livesport.expressen.se", "https://livesport.expressen.se/"]
    };

    private static void EnsureAllServicesPresent(AppConfigSnapshot snapshot)
    {
        var defaults = ServiceConfig.CreateDefaults();
        foreach (var def in defaults)
        {
            var existing = snapshot.Services.FirstOrDefault(s => s.ServiceId == def.ServiceId);
            if (existing is null)
            {
                snapshot.Services.Add(def);
            }
            else
            {
                existing.BlockedSports ??= [];
                if (LegacyDefaultUrls.TryGetValue(def.ServiceId, out var legacyUrls) &&
                    legacyUrls.Any(old => old.Equals(existing.SportsHubUrl?.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    existing.SportsHubUrl = def.SportsHubUrl;
                    existing.LoginUrl = def.LoginUrl;
                }
            }

            if (!snapshot.RankingProfile.ServicePriorities.Contains(def.ServiceId))
            {
                snapshot.RankingProfile.ServicePriorities.Add(def.ServiceId);
            }
        }
    }

    private static void EnsureDefaultSportsPresent(RankingProfile profile)
    {
        if (profile.SportPriorities.Count == 0)
        {
            profile.SportPriorities = RankingProfile.CreateDefault().SportPriorities;
        }
        else
        {
            foreach (var sport in SportsKnowledgeBase.KnownSports)
            {
                if (!profile.SportPriorities.Any(s => s.Equals(sport, StringComparison.OrdinalIgnoreCase)))
                {
                    profile.SportPriorities.Add(sport);
                }
            }
        }

        if (profile.ServicePriorities.Count == 0)
        {
            profile.ServicePriorities = RankingProfile.CreateDefault().ServicePriorities;
        }

        profile.PenaltyRules ??= [];
    }
}
