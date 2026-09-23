using SteamFusion.Core;

namespace SteamFusion.Windows;

public static class LaunchOptionsStore
{
    public static List<LibrarySnapshot> Read(Configuration config, string? directory = null)
    {
        var snapshots = new List<LibrarySnapshot>();
        foreach (var account in config.Accounts)
        {
            var path = Path.Combine(directory ?? Discovery.DataDirectory, "catalogs", account.SteamId + ".json");
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                var snapshot = Json.Decode<LibrarySnapshot>(File.ReadAllText(path)); snapshot.Validate();
                if (snapshot.SteamId == account.SteamId) snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { }
        }
        return snapshots;
    }
    public static void Refresh(Configuration config)
    {
        var available = AccountLaunch.Available(config, Read(config), DateTimeOffset.UtcNow);
        ConfigurationStore.AtomicWrite(Path.Combine(Discovery.DataDirectory, "launch-options.json"), Json.Encode(new
        {
            schemaVersion = 1, capturedAt = DateTimeOffset.UtcNow,
            games = config.Games.Where(g => g.Enabled && g.Saves != SaveMode.FixedEnvironment && available.GetValueOrDefault(g.AppId)?.Count == 2)
                .Select(g => new { g.AppId, accounts = available[g.AppId] })
        }));
    }
}
