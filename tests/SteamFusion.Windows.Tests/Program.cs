using SteamFusion.Core;
using SteamFusion.Windows;

if (args.Contains("--agent")) return AgentStartupTests.Agent();
if (args.Contains("--startup-job-worker")) return AgentStartupTests.Worker();
if (args.Contains("--agent-startup")) return AgentStartupTests.Run();
if (args.Contains("--pipe-identity")) return await PipeIdentityTests.Run();
if (args.Contains("--library-sync")) return LibrarySyncStoreTests.Run();

if (args.Contains("--selection-preflight"))
{
    var cfg = Discovery.Store.Load();
    var file = Path.Combine(Path.GetDirectoryName(cfg.SteamExe)!, "config", "loginusers.vdf");
    var original = File.ReadAllBytes(file);
    foreach (var account in cfg.Accounts)
    {
        var selected = SteamLoginUsers.Select(new System.Text.UTF8Encoding(false, true).GetString(original), account);
        var users = Vdf.Parse(selected.TrimStart('\uFEFF')).Children["users"];
        Require(users.Children[account.SteamId].Get("MostRecent") == "1" &&
            users.Children.Count(u => u.Value.Get("MostRecent") == "1") == 1, "Selection must be unique");
    }
    Require(original.SequenceEqual(File.ReadAllBytes(file)), "Preflight must not write Steam state");
    Console.WriteLine("PASS read-only preflight for " + cfg.Accounts.Count + " configured accounts; no Steam state changed.");
    return 0;
}
if (args.Contains("--account-selection")) return AccountSelectionTests.Run();

var root = Path.Combine(Path.GetTempPath(), "SteamFusion test 中文 " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var exe = Path.Combine(root, "SteamFusion.Cli.exe"); File.WriteAllText(exe, "fixture");
    var user = new Account("test", "76561198000000001", "test", "Test", "SF_Test");
    var config = new Configuration { SteamExe = Path.Combine(root, "steam.exe"), Accounts = [user],
        Games = [new(368340, "CrossCode 中文", user.Id) { Enabled = true }], DefaultNativeAccountId = user.Id };
    var path = Path.Combine(root, "userdata", "39734273", "config", "shortcuts.vdf");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var unrelated = new BinaryEntry(0, "0", new List<BinaryEntry> { new(1, "AppName", "Unrelated"), new(1, "exe", "other.exe") });
    var original = BinaryVdf.Write([new(0, "shortcuts", new List<BinaryEntry> { unrelated })]);
    File.WriteAllBytes(path, original);
    LibraryShortcuts.Install(config, exe);
    var first = File.ReadAllBytes(path);
    LibraryShortcuts.Install(config, exe);
    Require(first.SequenceEqual(File.ReadAllBytes(path)), "Repeated install must be idempotent");
    Require(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.bak").Any(), "Original must be backed up");
    LibraryShortcuts.Remove(config, exe);
    Require(original.SequenceEqual(File.ReadAllBytes(path)), "Remove must preserve unrelated entries exactly");
    var malformed = new byte[] { 0, 1, 2 }; File.WriteAllBytes(path, malformed);
    try { LibraryShortcuts.Install(config, exe); throw new Exception("Expected invalid VDF rejection"); }
    catch (InvalidDataException) { }
    Require(malformed.SequenceEqual(File.ReadAllBytes(path)), "Malformed file must not be overwritten");
    Require(NativeMethods.Quote(@"C:\a b\") == "\"C:\\a b\\\\\"", "Trailing slash quoting");
    NativeMethods.RequireOutsideSandbox();
    Console.WriteLine("PASS Windows: shortcut install/remove/backup/idempotence/malformed-file protection; Unicode paths; native context.");
    return 0;
}
finally { Directory.Delete(root, true); }
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
