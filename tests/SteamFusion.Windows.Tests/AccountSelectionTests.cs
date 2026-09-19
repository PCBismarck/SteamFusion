using Microsoft.Win32;
using SteamFusion.Core;
using SteamFusion.Windows;
using System.Text;

static class AccountSelectionTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamFusion selection 中文 " + Guid.NewGuid().ToString("N"));
        var registryPath = @"Software\SteamFusion\Tests\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var file = Path.Combine(root, "config", "loginusers.vdf");
        var exe = Path.Combine(root, "steam.exe");
        var target = new Account("test", "76561198000000001", "test", "Test", "SF_Test");
        var original = Encoding.UTF8.GetBytes("\uFEFF// keep\r\n\"users\" { \"76561198000000001\" { \"AccountName\" \"test\" \"MostRecent\" \"0\" } }");
        using var key = Registry.CurrentUser.CreateSubKey(registryPath);
        void Reset() { File.WriteAllBytes(file, original); key.SetValue("AutoLoginUser", "previous"); key.DeleteValue("RememberPassword", false); }
        try
        {
            Reset();
            NativeAccountSelection.Select(exe, target, key, () => { });
            Require((string?)key.GetValue("AutoLoginUser") == "test", "Target registry account");
            Require(key.GetValueKind("RememberPassword") == RegistryValueKind.DWord && (int)key.GetValue("RememberPassword")! == 1, "Remember flag type");
            Require(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.bak").Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "Exact backup including BOM");
            var selected = File.ReadAllBytes(file);
            NativeAccountSelection.Select(exe, target, key, () => { });
            Require(selected.SequenceEqual(File.ReadAllBytes(file)), "Repeated selection preserves file");
            Reset();
            MustFail(() => NativeAccountSelection.Select(exe, target, key, () => throw new IOException("Client still running")));
            Require(original.SequenceEqual(File.ReadAllBytes(file)) && (string?)key.GetValue("AutoLoginUser") == "previous", "Running process guard prevents mutation");
            int checks = 0;
            MustFail(() => NativeAccountSelection.Select(exe, target, key, () => { if (++checks == 2) File.AppendAllText(file, "\n// external change"); }));
            Require(File.ReadAllText(file).EndsWith("// external change") && (string?)key.GetValue("AutoLoginUser") == "previous", "Concurrent file change is retained");
            Reset();
            using (var readOnly = Registry.CurrentUser.OpenSubKey(registryPath, writable: false)!)
                MustFail(() => NativeAccountSelection.Select(exe, target, readOnly, () => { }));
            Require(original.SequenceEqual(File.ReadAllBytes(file)), "Registry failure restores original file");
            Require((string?)key.GetValue("AutoLoginUser") == "previous" && key.GetValue("RememberPassword") is null, "Registry failure preserves previous values");
            File.WriteAllText(file, "users {");
            MustFail(() => NativeAccountSelection.Select(exe, target, key, () => { }));
            Require(File.ReadAllText(file) == "users {" && (string?)key.GetValue("AutoLoginUser") == "previous", "Malformed input not overwritten");
            Require(!Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Any(), "No temporary files left");
            Console.WriteLine("PASS Windows built-in account selection: isolated files/registry; backup; idempotence; process guard; concurrent edit; registry failure rollback; malformed input; cleanup.");
            return 0;
        }
        finally { key.Close(); Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); Directory.Delete(root, true); }
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void MustFail(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected operation to be rejected");
    }
}
