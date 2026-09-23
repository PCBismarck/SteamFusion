using SteamFusion.Core;

internal static class AccountLaunchTests
{
    public static void Register(Action<string, Action> test)
    {
        var a = new Account("a", "76561198000000001", "a", "A", "Box_A");
        var b = new Account("b", "76561198000000002", "b", "B", "Box_B");
        var game = new Game(10, "Game", "b") { Enabled = true, Mode = RunMode.NativeOnly, Saves = SaveMode.SteamCloud, AutoSwap = false };
        var config = new Configuration { Accounts = [a, b], DefaultNativeAccountId = "a", Games = [game] };
        var now = DateTimeOffset.UtcNow;
        LibrarySnapshot Snap(Account account, bool? license = true) => new(1, "steam-client", account.SteamId, now, [new(10, "Game", license, 0, true, true)]);
        void Check(bool ok) { if (!ok) throw new Exception("Account launch assertion failed"); }
        void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected refusal"); }
        test("Explicit account launch changes only the in-memory route and resets save consent", () =>
        {
            var plan = AccountLaunch.Prepare(config, 10, a.SteamId, [Snap(a), Snap(b)], now);
            Check(plan.Configuration.Games.Single() == game with { AccountId = "a", Saves = SaveMode.Unknown });
            Check(plan.ConfirmAccountChange && config.Games.Single() == game);
            var original = AccountLaunch.Prepare(config, 10, b.SteamId, [Snap(a), Snap(b)], now);
            Check(!original.ConfirmAccountChange && original.Configuration.Games.Single() == game);
        });
        test("Both-account launches reject unlicensed unknown stale missing and foreign targets", () =>
        {
            foreach (var bad in new[] { Snap(a, false), Snap(a, null), Snap(a) with { CapturedAt = now.AddDays(-8) }, Snap(a) with { CapturedAt = now.AddMinutes(6) } })
                Reject(() => AccountLaunch.Prepare(config, 10, a.SteamId, [bad, Snap(b)], now));
            Reject(() => AccountLaunch.Prepare(config, 10, a.SteamId, [Snap(b)], now));
            Reject(() => AccountLaunch.Prepare(config, 10, "unknown", [Snap(a), Snap(b)], now));
        });
        test("Disabled and fixed-save rules cannot be bypassed by an account button", () =>
        {
            Reject(() => AccountLaunch.Prepare(config with { Games = [game with { Enabled = false }] }, 10, a.SteamId, [Snap(a), Snap(b)], now));
            Reject(() => AccountLaunch.Prepare(config with { Games = [game with { Saves = SaveMode.FixedEnvironment, FixedEnvironment = "native" }] }, 10, a.SteamId, [Snap(a), Snap(b)], now));
        });
        test("Account choice is rechecked when a previously available license is revoked", () =>
        {
            Check(AccountLaunch.Available(config, [Snap(a), Snap(b)], now)[10].Count == 2);
            Reject(() => AccountLaunch.Prepare(config, 10, b.SteamId, [Snap(a), Snap(b, false)], now));
        });
    }
}
