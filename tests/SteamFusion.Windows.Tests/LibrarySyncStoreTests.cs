using SteamFusion.Core;
using SteamFusion.Windows;

internal static class LibrarySyncStoreTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamFusion-sync-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configStore = new ConfigurationStore(root);
            var a = new Account("a", "76561198000000001", "a", "A", "Box_A");
            var b = new Account("b", "76561198000000002", "b", "B", "Box_B");
            var game = new Game(10, "Test", "b") { Enabled = true };
            var config = new Configuration { Accounts = [a, b], Games = [game], DefaultNativeAccountId = "a" };
            configStore.Save(config); var original = File.ReadAllText(configStore.ConfigPath);
            Configuration? published = null;
            var sync = new LibrarySyncStore(configStore, Path.Combine(root, "backups"), c => published = c);
            sync.Begin(); var started = sync.Session().StartedAt;
            if (sync.Preview().Ready) throw new Exception("Missing snapshots accepted.");
            if (new LibrarySyncStore(configStore, root, _ => { }).Session().StartedAt != started) throw new Exception("Session not persisted.");
            void Snapshot(Account account, uint id)
            {
                var data = new LibrarySnapshot(1, "steam-client", account.SteamId, DateTimeOffset.UtcNow,
                    [new(id, "Game", true, 0, false, true)]) { IncludesHiddenGames = true };
                ConfigurationStore.AtomicWrite(Path.Combine(root, "catalogs", account.SteamId + ".json"), Json.Encode(data));
            }
            Snapshot(a, 10); Snapshot(b, 20);
            var p = sync.Preview();
            var result = sync.Apply(p.Fingerprint, [10]);
            if (File.ReadAllText(Path.Combine(result.BackupDirectory, "config-before.json")) != original ||
                published?.Games.Single().AccountId != "a" || configStore.Load().Games.Single().AccountId != "a") throw new Exception("Backup or apply failed.");
            // A changed license after review cannot write anything.
            configStore.Save(config); p = sync.Preview(); Snapshot(a, 30);
            try { sync.Apply(p.Fingerprint, [10]); throw new Exception("Stale preview accepted."); }
            catch (InvalidOperationException) { }
            if (File.ReadAllText(configStore.ConfigPath) != original) throw new Exception("Stale apply modified configuration.");
            // Failed plugin publication restores both config and published routing.
            Snapshot(a, 10); var calls = 0;
            var failing = new LibrarySyncStore(configStore, Path.Combine(root, "backups"), c => { if (++calls == 1) throw new IOException("fixture failure"); published = c; });
            p = failing.Preview();
            try { failing.Apply(p.Fingerprint, [10]); throw new Exception("Expected publication failure."); } catch (IOException) { }
            if (File.ReadAllText(configStore.ConfigPath) != original || published?.Games.Single() != game || calls != 2) throw new Exception("Rollback failed.");
            Snapshot(b, 10);
            if (sync.Preview().Changes.Single().CanApply) throw new Exception("Default mode changed a valid route.");
            p = sync.Preview(true); sync.Apply(p.Fingerprint, [10], true);
            if (configStore.Load().Games.Single().AccountId != "a") throw new Exception("Explicit native preference was not applied.");
            Console.WriteLine("PASS library sync store: persisted collection session, fresh preview, exact configuration backup, selected apply, stale evidence rejection and publication rollback.");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
