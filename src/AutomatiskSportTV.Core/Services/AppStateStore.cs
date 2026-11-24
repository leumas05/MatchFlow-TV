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
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configFilePath;

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
    }

    public string ConfigFilePath => _configFilePath;

    public AppConfigSnapshot Load()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                var defaults = new AppConfigSnapshot();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_configFilePath);
            var snapshot = JsonSerializer.Deserialize<AppConfigSnapshot>(json, JsonOptions) ?? new AppConfigSnapshot();
            EnsureAllServicesPresent(snapshot);
            EnsureDefaultSportsPresent(snapshot.RankingProfile);
            return snapshot;
        }
        catch
        {
            return new AppConfigSnapshot();
        }
    }

    public void Save(AppConfigSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_configFilePath}.tmp";
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _configFilePath, overwrite: true);
    }

    private static void EnsureAllServicesPresent(AppConfigSnapshot snapshot)
    {
        var defaults = ServiceConfig.CreateDefaults();
        foreach (var def in defaults)
        {
            if (!snapshot.Services.Any(s => s.ServiceId == def.ServiceId))
            {
                snapshot.Services.Add(def);
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
    }
}
