using SteamFusion.Core;
using SteamFusion.Windows;

if (args.Contains("--watt-help"))
{
    var pid = WattStore.Activate("--help");
    Console.WriteLine("Store activation accepted, PID=" + pid);
    return 0;
}
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
    Console.WriteLine("Watt Store detected=" + WattStore.Installed());
    return 0;
}
finally { Directory.Delete(root, true); }
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
