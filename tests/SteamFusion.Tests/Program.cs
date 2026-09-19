using SteamFusion.Core;

var a = new Account("a", "76561198000000001", "account_a", "A", "Box_A");
var b = new Account("b", "76561198000000002", "account_b", "B", "Box_B");
var game = new Game(368340, "CrossCode", "b") { Enabled = true };
var config = new Configuration { Accounts = [a, b], Games = [game], DefaultNativeAccountId = "a" };
var nativeA = Make("native", a, 100);
var boxB = Make("Box_B", b, 200);
var tests = new List<(string, Func<Task>)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void Async(string name, Func<Task> run) => tests.Add((name, run));

Test("Routes B to its already logged-in sandbox", () => Equal("Box_B", RoutePlanner.Plan(config, game, new([nativeA, boxB])).Environment));
Test("Native-only B requires an account exchange", () => Check(RoutePlanner.Plan(config, game with { Mode = RunMode.NativeOnly }, new([nativeA, boxB])).RequiresSwap));
Test("Already-native B does not switch again", () => Check(!RoutePlanner.Plan(config, game with { Mode = RunMode.NativeOnly }, new([Make("native", b, 100)])).RequiresSwap));
Test("Stopped Steam requires selecting the account instead of trusting its last auto-login", () =>
    Check(RoutePlanner.Plan(config, game with { AccountId = "a" }, new([])).RequiresSwap));
Test("Disabled ownership mapping cannot launch", () => Throws(() => RoutePlanner.Plan(config, game with { Enabled = false }, new([nativeA]))));
Test("Unknown native account is never guessed", () => Throws(() => RoutePlanner.Plan(config, game, new([nativeA with { SteamId = "76561198099999999" }]))));
Test("Unmanaged clients stop all routing", () => Throws(() => RoutePlanner.Plan(config, game, new([nativeA], true))));
Test("Fixed save environment prevents accidental migration", () => Throws(() => RoutePlanner.Plan(config,
    game with { Mode = RunMode.NativeOnly, Saves = SaveMode.FixedEnvironment, FixedEnvironment = "Box_B" }, new([nativeA, boxB]))));
Test("Duplicate account or AppID mappings rejected", () => { Throws(() => (config with { Games = [game, game] }).Validate()); Throws(() => (config with { Accounts = [a, a] }).Validate()); });
Test("Shell metacharacters rejected in Watt account field", () => Throws(() => (config with { Accounts = [a with { LoginName = "a & whoami" }, b] }).Validate()));

Async("Routed launch uses the correct instance without shutting down either client", async () =>
{
    var fake = new Fake([nativeA, boxB]); var router = new Router(fake);
    Check((await router.LaunchAsync(config, game.AppId, Guid.NewGuid())).Success);
    Equal("launch:Box_B:368340", fake.Events.Last()); Check(!fake.Events.Any(e => e.StartsWith("stop:")));
});
Async("Exchange closes boxes before native, then chooses account before launching", async () =>
{
    var fake = new Fake([nativeA, boxB]); var router = new Router(fake);
    var cfg = config with { Games = [game with { Mode = RunMode.NativeOnly }] };
    Check((await router.LaunchAsync(cfg, game.AppId, Guid.NewGuid())).Success);
    Equal("confirm,stop:Box_B,stop:native,switch:b,ready:native,ready:Box_A,ready:native,launch:native:368340", string.Join(',', fake.Events));
});
Async("Running game blocks Watt and shutdown", async () =>
{
    var fake = new Fake([nativeA with { RunningGames = [620] }, boxB]);
    Check(!(await new Router(fake).SwapAsync(config, "b", Guid.NewGuid())).Success); Equal(0, fake.Events.Count);
});
Async("Declining sync confirmation cannot close clients", async () =>
{
    var fake = new Fake([nativeA, boxB]) { Confirm = false };
    Check(!(await new Router(fake).SwapAsync(config, "b", Guid.NewGuid())).Success);
    Equal("confirm", string.Join(',', fake.Events));
});
Async("Unknown save environment requires confirmation before any launch or exchange", async () =>
{
    var fake = new Fake([nativeA, boxB]) { ConfirmSave = false };
    Equal("cancelled", (await new Router(fake).LaunchAsync(config, game.AppId, Guid.NewGuid())).Code);
    Equal(0, fake.Events.Count);
});
Async("A game started while a dialog was open blocks the exchange", async () =>
{
    var fake = new Fake([nativeA, boxB]); fake.OnConfirm = () => fake.Instances[0] = nativeA with { RunningGames = [620] };
    Check(!(await new Router(fake).SwapAsync(config, "b", Guid.NewGuid())).Success);
    Equal("confirm", string.Join(',', fake.Events));
});
Async("Failure to close a client cannot call Watt", async () =>
{
    var fake = new Fake([nativeA, boxB]) { FailStop = true };
    Check(!(await new Router(fake).SwapAsync(config, "b", Guid.NewGuid())).Success);
    Check(!fake.Events.Any(e => e.StartsWith("switch:")));
});
Async("Wrong identity after ready cannot launch", async () =>
{
    var fake = new Fake([nativeA, boxB]) { WrongIdentity = true };
    Check(!(await new Router(fake).LaunchAsync(config, game.AppId, Guid.NewGuid())).Success);
    Check(!fake.Events.Any(e => e.StartsWith("launch:")));
});
Async("Duplicate request ID only launches once", async () =>
{
    var fake = new Fake([nativeA, boxB]); var router = new Router(fake); var id = Guid.NewGuid();
    Check((await router.LaunchAsync(config, game.AppId, id)).Success);
    Check((await router.LaunchAsync(config, game.AppId, id)).Success);
    Equal(1, fake.Events.Count(e => e.StartsWith("launch:")));
    Equal("request_conflict", (await router.SwapAsync(config, "a", id)).Code);
});
Async("Concurrent launch is rejected while first operation waits", async () =>
{
    var fake = new Fake([nativeA, boxB]) { WaitReady = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    var router = new Router(fake); var first = router.LaunchAsync(config, game.AppId, Guid.NewGuid());
    Equal("busy", (await router.LaunchAsync(config, game.AppId, Guid.NewGuid())).Code);
    fake.WaitReady.SetResult(); Check((await first).Success);
});
Async("Manual mode returns an actionable result without side effects", async () =>
{
    var fake = new Fake([nativeA, boxB]); var cfg = config with { Games = [game with { Mode = RunMode.NativeOnly, AutoSwap = false }] };
    Equal("manual_swap", (await new Router(fake).LaunchAsync(cfg, game.AppId, Guid.NewGuid())).Code); Equal(0, fake.Events.Count);
});
Async("Failed secondary sandbox does not invalidate the ready native account", async () =>
{
    var fake = new Fake([nativeA, boxB]) { FailBoxA = true };
    var result = await new Router(fake).SwapAsync(config, "b", Guid.NewGuid());
    Check(result.Success); Check(result.Message.Contains("Sandbox unavailable"));
    Check(fake.Instances.Any(i => i.IsNative && i.SteamId == b.SteamId));
});

Test("Text VDF preserves Windows backslashes and escaped quotes", () =>
{
    var parsed = Vdf.Parse("\"root\" { // a comment\n\"path\" \"F:\\\\SteamLibrary\" \"name\" \"A \\\"B\\\"\" }");
    Equal(@"F:\SteamLibrary", parsed.Children["root"].Get("path")); Equal("A \"B\"", parsed.Children["root"].Get("name"));
    Throws(() => Vdf.Parse("\"root\" { \"key\""));
});
Test("Binary VDF preserves unknown-to-us fields byte-for-byte", () =>
{
    List<BinaryEntry> nodes = [new(0, "shortcuts", new List<BinaryEntry> { new(0, "0", new List<BinaryEntry>
    { new(1, "AppName", "中文游戏"), new(2, "appid", new byte[] { 0, 0, 0, 128 }), new(7, "opaque", new byte[8]), new(0, "tags", new List<BinaryEntry>()) }) })];
    var bytes = BinaryVdf.Write(nodes); Check(bytes.SequenceEqual(BinaryVdf.Write(BinaryVdf.Read(bytes))));
    Throws(() => BinaryVdf.Read(bytes[..^1])); Throws(() => BinaryVdf.Read([9, 0, 8]));
});
Test("Atomic config save keeps the previous version as a backup", () =>
{
    var temp = Path.Combine(Path.GetTempPath(), "steamfusion-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new ConfigurationStore(temp); store.Save(config); store.Save(config with { KeepBothOnline = false });
        Check(!store.Load().KeepBothOnline); Check(Json.Decode<Configuration>(File.ReadAllText(store.ConfigPath + ".bak")).KeepBothOnline);
    }
    finally { Directory.Delete(temp, true); }
});

var catalogNow = DateTimeOffset.UtcNow;
LibrarySnapshot Library(Account owner, params LibraryGame[] entries) => new(1, "steam-client", owner.SteamId, catalogNow, [.. entries]);
LibraryGame Entry(uint id, bool? subscribed = true, uint owner = 0, bool? cloud = null) => new(id, "Game " + id, subscribed, owner, true, cloud);
Test("Installed files and foreign ownership hints never grant account access", () =>
{
    var preview = LibraryCatalog.Preview(config, [Library(a, Entry(570, false, 123), Entry(620, null))], [(700U, "Installed only")], catalogNow);
    Equal(0, preview.Importable); Equal(3, preview.Unresolved);
});
Test("Family-shared access routes through the subscribed account", () =>
{
    var preview = LibraryCatalog.Preview(config, [Library(b, Entry(570, true, 123))], [], catalogNow);
    var result = LibraryCatalog.Import(config, preview);
    var imported = result.Configuration.Games.Single(g => g.AppId == 570);
    Equal("b", imported.AccountId); Check(preview.Games.Single(g => g.AppId == 570).Access[0].Borrowed);
    Equal(RunMode.Auto, imported.Mode); Equal(SaveMode.Unknown, imported.Saves); Check(imported.FixedEnvironment is null);
    Equal("Box_B", RoutePlanner.Plan(result.Configuration, imported, new([nativeA, boxB])).Environment);
});
Test("Overlapping libraries prefer the configured default without changing existing rules", () =>
{
    var cfg = config with { Games = [game with { Enabled = false, Mode = RunMode.NativeOnly, AutoSwap = false, Saves = SaveMode.FixedEnvironment, FixedEnvironment = "native" }] };
    var preview = LibraryCatalog.Preview(cfg, [Library(a, Entry(570), Entry(368340)), Library(b, Entry(570), Entry(368340))], [], catalogNow);
    var result = LibraryCatalog.Import(cfg, preview);
    Equal("a", result.Configuration.Games.Single(g => g.AppId == 570).AccountId);
    Equal(cfg.Games[0], result.Configuration.Games.Single(g => g.AppId == 368340));
    var repeat = LibraryCatalog.Import(result.Configuration, LibraryCatalog.Preview(result.Configuration, [Library(a, Entry(570))], [], catalogNow));
    Equal(0, repeat.Added); Equal(2, repeat.Configuration.Games.Count);
});
Test("Only explicit enabled cloud evidence selects cloud save policy", () =>
{
    var result = LibraryCatalog.Import(config, LibraryCatalog.Preview(config, [Library(b, Entry(570, cloud: true), Entry(620, cloud: false))], [], catalogNow));
    Equal(SaveMode.SteamCloud, result.Configuration.Games.Single(g => g.AppId == 570).Saves);
    Equal(SaveMode.Unknown, result.Configuration.Games.Single(g => g.AppId == 620).Saves);
});
Test("Stale, future and unconfigured account snapshots cannot auto-import", () =>
{
    var preview = LibraryCatalog.Preview(config, [Library(a, Entry(570)) with { CapturedAt = catalogNow.AddDays(-8) },
        Library(b, Entry(620)) with { CapturedAt = catalogNow.AddMinutes(10) },
        Library(a, Entry(700)) with { SteamId = "76561198000000009" }], [], catalogNow);
    Equal(0, preview.Importable); Equal(2, preview.MissingAccountIds.Count);
});
Test("Bulk selections reject unsupported account assignment and preserve the original config", () =>
{
    var preview = LibraryCatalog.Preview(config, [Library(b, Entry(570))], [], catalogNow);
    Throws(() => LibraryCatalog.Import(config, preview, [new(570, "a")]));
    Throws(() => LibraryCatalog.Import(config, preview, [new(570, "b"), new(570, "b")]));
    Equal(1, config.Games.Count);
});
Test("Malformed catalog cannot partially replace a previous snapshot", () =>
{
    Throws(() => Library(a, Entry(570), Entry(570)).Validate());
    Throws(() => (Library(a, Entry(570)) with { Source = "last-owner" }).Validate());
});

Async("Shared downloads open the sandbox library page without launching or swapping", async () =>
{
    var fake = new Fake([nativeA, boxB]) { ConfirmSave = false };
    var cfg = config with { SharedLibraryDownloads = true, Games = [game with { Mode = RunMode.NativeOnly, AutoSwap = false }] };
    Check((await new Router(fake).OpenDownloadAsync(cfg, game.AppId, Guid.NewGuid())).Success);
    Equal("library:Box_B:368340", fake.Events.Last());
    Check(!fake.Events.Any(e => e.StartsWith("launch:") || e.StartsWith("switch:")));
    Equal(RunMode.NativeOnly, cfg.Games[0].Mode);
});
Async("Without shared writes downloads select native and retain the gameplay save policy", async () =>
{
    var fake = new Fake([nativeA, boxB]);
    var cfg = config with { Games = [game with { Saves = SaveMode.FixedEnvironment, FixedEnvironment = "Box_B" }] };
    Check((await new Router(fake).OpenDownloadAsync(cfg, game.AppId, Guid.NewGuid())).Success);
    Check(fake.Events.Contains("switch:b")); Equal("library:native:368340", fake.Events.Last());
    Equal("Box_B", cfg.Games[0].FixedEnvironment);
});
Async("Download navigation rejects wrong identity and never launches the game", async () =>
{
    var fake = new Fake([nativeA, boxB]) { WrongIdentity = true };
    Check(!(await new Router(fake).OpenDownloadAsync(config with { SharedLibraryDownloads = true }, game.AppId, Guid.NewGuid())).Success);
    Check(!fake.Events.Any(e => e.StartsWith("library:") || e.StartsWith("launch:")));
});
Async("Download navigation is deduplicated and a running game prevents a required swap", async () =>
{
    var fake = new Fake([nativeA, boxB]); var router = new Router(fake); var id = Guid.NewGuid();
    var cfg = config with { SharedLibraryDownloads = true };
    Check((await router.OpenDownloadAsync(cfg, game.AppId, id)).Success);
    Check((await router.OpenDownloadAsync(cfg, game.AppId, id)).Success);
    Equal(1, fake.Events.Count(e => e.StartsWith("library:")));
    fake = new Fake([nativeA with { RunningGames = [570] }, boxB]);
    Check(!(await new Router(fake).OpenDownloadAsync(config, game.AppId, Guid.NewGuid())).Success);
    Check(!fake.Events.Any(e => e.StartsWith("switch:") || e.StartsWith("library:")));
});

var failed = 0;
GameDataExportTests.Register(Test);
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

static Instance Make(string env, Account account, int pid) => new(env, pid, pid, account.SteamId, true, [], true, "test");
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection"); }

sealed class Fake(List<Instance> instances) : IRuntime
{
    public List<Instance> Instances { get; } = instances;
    public List<string> Events { get; } = [];
    public bool Confirm = true, ConfirmSave = true, FailStop, WrongIdentity, FailBoxA;
    public Action? OnConfirm;
    public TaskCompletionSource? WaitReady;
    public Task<Snapshot> ObserveAsync(Configuration c, CancellationToken ct) => Task.FromResult(new Snapshot([.. Instances]));
    public Task<bool> ConfirmSaveEnvironmentAsync(Game g, Route r, CancellationToken ct) => Task.FromResult(ConfirmSave);
    public async Task<Instance> EnsureReadyAsync(Configuration c, Account account, string env, CancellationToken ct)
    {
        if (WaitReady is not null) await WaitReady.Task.WaitAsync(ct);
        Events.Add("ready:" + env);
        if (FailBoxA && env == "Box_A") throw new IOException("Sandbox unavailable");
        var instance = Instances.FirstOrDefault(i => i.Environment == env);
        if (instance is null) { instance = new(env, 300 + Instances.Count, 300, account.SteamId, true, [], true, "test"); Instances.Add(instance); }
        return WrongIdentity ? instance with { SteamId = "76561198099999999" } : instance;
    }
    public Task<bool> ConfirmSwitchAsync(Configuration c, Snapshot s, Account target, CancellationToken ct)
    { Events.Add("confirm"); OnConfirm?.Invoke(); return Task.FromResult(Confirm); }
    public Task StopAsync(Configuration c, Instance i, CancellationToken ct)
    { Events.Add("stop:" + i.Environment); if (FailStop) throw new IOException("Still running"); Instances.Remove(i); return Task.CompletedTask; }
    public Task SelectNativeAccountAsync(Configuration c, Account a, CancellationToken ct)
    { Events.Add("switch:" + a.Id); Instances.Add(new("native", 999, 999, a.SteamId, true, [], true, "test")); return Task.CompletedTask; }
    public Task LaunchAsync(Configuration c, Instance i, uint appId, CancellationToken ct)
    { Events.Add($"launch:{i.Environment}:{appId}"); return Task.CompletedTask; }
    public Task OpenLibraryAsync(Configuration c, Instance i, uint appId, CancellationToken ct)
    { Events.Add($"library:{i.Environment}:{appId}"); return Task.CompletedTask; }
}
