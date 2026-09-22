using SteamFusion.Core;

internal static class LibrarySyncTests
{
    private static readonly Account A = new("a", "76561198000000001", "a", "A", "Box_A"), B = new("b", "76561198000000002", "b", "B", "Box_B");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Game Game = new(10, "Test", "b") { Enabled = true, Mode = RunMode.NativeOnly, AutoSwap = false, PauseOtherSandbox = true, Saves = SaveMode.SteamCloud };
    private static Configuration Config => new() { Accounts = [A, B], DefaultNativeAccountId = "a", Games = [Game] };
    private static LibraryGame Entry(uint id, bool? access = true) => new(id, "Game " + id, access, 0, false, true);
    private static LibrarySnapshot Snapshot(Account a, params LibraryGame[] games) => new(1, "steam-client", a.SteamId, Now, games.ToList()) { IncludesHiddenGames = true };
    private static LibrarySyncPreview Preview(Configuration config, params LibrarySnapshot[] data) => LibrarySync.Preview(config, data, Now.AddMinutes(-2), Now);
    private static void Check(bool condition) { if (!condition) throw new Exception("Library sync assertion failed"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; } throw new Exception("Expected rejection"); }
    public static void Register(Action<string, Action> test)
    {
        test("Family transfer proposes reassignment and preserves runtime options while resetting save consent", () =>
        {
            var p = Preview(Config, Snapshot(A, Entry(10)), Snapshot(B, Entry(20)));
            var change = p.Changes.Single(c => c.AppId == 10);
            Check(change.Action == LibrarySyncAction.Reassign && change.Before == Game);
            Check(change.After == Game with { AccountId = "a", Saves = SaveMode.Unknown });
            var updated = LibrarySync.Apply(Config, p, p.Fingerprint, [10]);
            Check(updated.Games.Count == 1 && updated.Games[0].AccountId == "a" && Config.Games[0] == Game);
        });
        test("New licenses are added; lost licenses disable without deleting configuration", () =>
        {
            var p = Preview(Config, Snapshot(A, Entry(20)), Snapshot(B, Entry(30)));
            Check(p.Changes.Single(c => c.AppId == 10).Action == LibrarySyncAction.Disable);
            var updated = LibrarySync.Apply(Config, p, p.Fingerprint, [10, 20, 30]);
            Check(updated.Games.Count == 3 && updated.Games.Single(g => g.AppId == 10) == Game with { Enabled = false });
        });
        test("Valid manual account, disabled rules and fixed save environments are preserved", () =>
        {
            Check(Preview(Config, Snapshot(A, Entry(10)), Snapshot(B, Entry(10))).Changes[0].Action == LibrarySyncAction.Keep);
            var disabled = Config with { Games = [Game with { Enabled = false }] };
            Check(Preview(disabled, Snapshot(A, Entry(10)), Snapshot(B, Entry(20))).Changes.Single(c => c.AppId == 10).Action == LibrarySyncAction.Keep);
            var fixedSave = Config with { Games = [Game with { Saves = SaveMode.FixedEnvironment, FixedEnvironment = "Box_B" }] };
            Check(Preview(fixedSave, Snapshot(A, Entry(10)), Snapshot(B, Entry(20))).Changes.Single(c => c.AppId == 10).Action == LibrarySyncAction.Review);
        });
        test("Unknown original license cannot trigger reassignment or disabling", () =>
        {
            foreach (bool? other in new bool?[] { true, false, null })
                Check(Preview(Config, Snapshot(A, Entry(10, other)), Snapshot(B, Entry(10, null))).Changes[0].Action == LibrarySyncAction.Review);
            Check(Preview(Config, Snapshot(A, Entry(10, null)), Snapshot(B, Entry(10, false))).Changes[0].Action == LibrarySyncAction.Review);
        });
        test("Both fresh complete nonempty account records are required", () =>
        {
            var good = Snapshot(A, Entry(10));
            foreach (var bad in new[] { Snapshot(B, Entry(20)) with { IncludesHiddenGames = false },
                Snapshot(B, Entry(20)) with { CapturedAt = Now.AddMinutes(-3) }, Snapshot(B, Entry(20)) with { CapturedAt = Now.AddMinutes(6) },
                Snapshot(B), Snapshot(B, Entry(20), Entry(20)) })
            { var p = Preview(Config, good, bad); Check(!p.Ready && p.Changes.Count == 0); Reject(() => LibrarySync.Apply(Config, p, p.Fingerprint, [10])); }
            Check(!Preview(Config, good).Ready);
            Check(!LibrarySync.Preview(Config, [good with { CapturedAt = Now.AddHours(-25) }, Snapshot(B, Entry(20))], Now.AddDays(-2), Now).Ready);
        });
        test("Changes to reviewed licenses or settings invalidate application but identical recaptures do not", () =>
        {
            var a = Snapshot(A, Entry(10)); var b = Snapshot(B, Entry(20));
            var p = Preview(Config, a, b);
            Check(Preview(Config, a with { CapturedAt = Now.AddSeconds(1) }, b).Fingerprint == p.Fingerprint);
            var changed = Preview(Config, Snapshot(A, Entry(10, false)), b);
            Reject(() => LibrarySync.Apply(Config, changed, p.Fingerprint, [10]));
            var cfg = Config with { Games = [Game with { AutoSwap = true }] };
            Reject(() => LibrarySync.Apply(cfg, Preview(cfg, a, b), p.Fingerprint, [10]));
        });
        test("Duplicate, unknown and review-only selections are rejected without partial mutation", () =>
        {
            var p = Preview(Config, Snapshot(A, Entry(20)), Snapshot(B, Entry(30)));
            foreach (uint[] ids in new uint[][] { [], [10, 10], [10, 999] }) Reject(() => LibrarySync.Apply(Config, p, p.Fingerprint, ids));
            var unknown = Preview(Config, Snapshot(A, Entry(10)), Snapshot(B, Entry(10, null)));
            Reject(() => LibrarySync.Apply(Config, unknown, unknown.Fingerprint, [10]));
            Check(Config.Games.Single() == Game);
        });
        test("New shared license prefers native account without changing existing valid manual selection", () =>
        {
            var p = Preview(Config, Snapshot(A, Entry(10), Entry(20) with { OwnerAccountId = 123 }), Snapshot(B, Entry(10), Entry(20)));
            Check(p.Changes.Single(c => c.AppId == 20).After!.AccountId == "a");
            Check(p.Changes.Single(c => c.AppId == 10).After == Game);
        });
    }
}
