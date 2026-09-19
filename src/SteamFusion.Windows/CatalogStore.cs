using SteamFusion.Core;

namespace SteamFusion.Windows;

public static class CatalogStore
{
    public static LibraryPreview Preview(Configuration config)
    {
        var snapshots = new List<LibrarySnapshot>();
        var errors = new List<string>();
        foreach (var account in config.Accounts)
        {
            var path = Path.Combine(Discovery.DataDirectory, "catalogs", account.SteamId + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("快照超过大小限制");
                var snapshot = Json.Decode<LibrarySnapshot>(File.ReadAllText(path));
                if (snapshot.SteamId != account.SteamId) throw new InvalidDataException("快照账号不一致");
                snapshot.Validate(); snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            { errors.Add($"{account.Name}：{ex.Message}"); }
        }
        var installed = Discovery.Scan().Games.Select(g => (g.AppId, g.Name));
        var preview = LibraryCatalog.Preview(config, snapshots, installed, DateTimeOffset.UtcNow);
        preview.Warnings.AddRange(errors);
        return preview;
    }

    public static LibraryImportResult Import(Configuration config, IEnumerable<LibrarySelection>? selection = null)
    {
        var preview = Preview(config);
        var result = LibraryCatalog.Import(config, preview, selection);
        if (result.Added > 0)
        {
            Discovery.Store.Save(result.Configuration);
            Discovery.WritePluginConfiguration(result.Configuration);
        }
        ConfigurationStore.AtomicWrite(Path.Combine(Discovery.DataDirectory, "catalog-import.json"), Json.Encode(new
        {
            at = DateTimeOffset.UtcNow, result.Added, result.Preserved, preview.Unresolved,
            preview.CapturedAccountIds, preview.MissingAccountIds, preview.Warnings
        }));
        return result;
    }
}
