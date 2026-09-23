using SteamFusion.Core;

namespace SteamFusion.Windows;

public sealed record LibrarySyncSession(DateTimeOffset StartedAt);
public sealed record LibrarySyncResult(int Changed, string BackupDirectory);

public sealed class LibrarySyncStore(ConfigurationStore store, string backupRoot, Action<Configuration> publish)
{
    public static LibrarySyncStore Current() => new(Discovery.Store,
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Data", "Backups")), Discovery.WritePluginConfiguration);
    private string SessionPath => Path.Combine(store.DirectoryPath, "library-sync-session.json");
    public LibrarySyncSession Begin()
    {
        var session = new LibrarySyncSession(DateTimeOffset.UtcNow);
        ConfigurationStore.AtomicWrite(SessionPath, Json.Encode(session)); return session;
    }
    public LibrarySyncSession Session() => File.Exists(SessionPath)
        ? Json.Decode<LibrarySyncSession>(File.ReadAllText(SessionPath)) : Begin();
    public LibrarySyncPreview Preview(bool preferNative = false)
    {
        var config = store.Load(); var snapshots = new List<LibrarySnapshot>();
        foreach (var account in config.Accounts)
        {
            var path = Path.Combine(store.DirectoryPath, "catalogs", account.SteamId + ".json");
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                var snapshot = Json.Decode<LibrarySnapshot>(File.ReadAllText(path));
                snapshot.Validate();
                if (snapshot.SteamId == account.SteamId) snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        return LibrarySync.Preview(config, snapshots, Session().StartedAt, DateTimeOffset.UtcNow, preferNative);
    }
    public LibrarySyncResult Apply(string reviewedFingerprint, IEnumerable<uint> selection, bool preferNative = false)
    {
        var selected = selection.ToArray();
        var originalText = File.ReadAllText(store.ConfigPath);
        var original = Json.Decode<Configuration>(originalText);
        var preview = Preview(preferNative);
        var updated = LibrarySync.Apply(original, preview, reviewedFingerprint, selected);
        if (File.ReadAllText(store.ConfigPath) != originalText) throw new IOException("配置已被其他窗口修改，请刷新预览。");
        var backup = Path.Combine(backupRoot, "library-sync-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "config-before.json"), originalText);
        File.WriteAllText(Path.Combine(backup, "changes.json"), Json.Encode(preview.Changes.Where(c => selected.Contains(c.AppId)).ToList()));
        try { store.Save(updated); publish(updated); }
        catch
        {
            ConfigurationStore.AtomicWrite(store.ConfigPath, originalText);
            publish(original);
            throw;
        }
        return new(selected.Length, backup);
    }
}
