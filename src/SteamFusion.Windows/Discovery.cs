using Microsoft.Win32;
using SteamFusion.Core;
using System.Diagnostics;

namespace SteamFusion.Windows;

public sealed record InstalledGame(uint AppId, string Name, string Directory, string LastOwner);
public sealed record DiscoveryReport(string SteamExe, string WattExe, string SandboxieStartExe,
    string DataDirectory, bool MillenniumInstalled, List<Account> Accounts, List<InstalledGame> Games);

public static class Discovery
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFusion");
    public static ConfigurationStore Store => new(DataDirectory);

    public static void WritePluginConfiguration(Configuration config)
    {
        Directory.CreateDirectory(Path.Combine(DataDirectory, "signals"));
        Directory.CreateDirectory(Path.Combine(DataDirectory, "catalogs"));
        ConfigurationStore.AtomicWrite(Path.Combine(DataDirectory, "plugin-routes.json"), Json.Encode(new
        {
            environment = "__ENVIRONMENT__",
            library = new { enabled = config.IntegrateLibrary, cliExe = Path.Combine(AppContext.BaseDirectory, "SteamFusion.Cli.exe"),
                uninstalled = config.UninstalledLibrary, hostSteamId = config.Accounts.FirstOrDefault(a => a.Id == config.DefaultNativeAccountId)?.SteamId },
            games = config.Games.Select(g => new { g.AppId, steamId = config.Accounts.Single(a => a.Id == g.AccountId).SteamId,
                gameName = g.Name,
                accountName = config.Accounts.Single(a => a.Id == g.AccountId).Name,
                mode = g.Mode == RunMode.NativeOnly ? "nativeOnly" : "auto",
                fixedEnvironment = g.Saves == SaveMode.FixedEnvironment ? g.FixedEnvironment : null, g.Enabled })
        }));
    }

    public static DiscoveryReport Scan()
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        var steamExe = Existing(steamPath is null ? "" : Path.Combine(steamPath, "steam.exe"));
        var sandbox = Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus", "Start.exe"));
        string watt = "";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var keyPath in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = hive.OpenSubKey(keyPath);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var display = key?.GetValue("DisplayName") as string ?? "";
                if (display.Contains("Watt", StringComparison.OrdinalIgnoreCase) || display.Contains("Steam++"))
                {
                    var location = key?.GetValue("InstallLocation") as string ?? "";
                    watt = Existing(Path.Combine(location, "Steam++.exe"));
                    if (watt.Length == 0)
                    {
                        var icon = (key?.GetValue("DisplayIcon") as string ?? "").Trim('"');
                        var index = icon.LastIndexOf(",", StringComparison.Ordinal);
                        watt = Existing(index >= 0 ? icon[..index].Trim('"') : icon);
                    }
                }
                if (display.Contains("Sandboxie", StringComparison.OrdinalIgnoreCase))
                {
                    var location = key?.GetValue("InstallLocation") as string ?? "";
                    var candidate = Existing(Path.Combine(location, "Start.exe"));
                    if (candidate.Length > 0) sandbox = candidate;
                }
            }
        }
        if (watt.Length == 0)
            foreach (var process in Process.GetProcessesByName("Steam++"))
                using (process) { try { watt = Existing(process.MainModule?.FileName ?? ""); } catch { } }
        if (WattStore.Installed() && (watt.Length == 0 || watt.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)))
            watt = WattStore.Setting;
        var accounts = new List<Account>();
        var games = new List<InstalledGame>();
        if (steamExe.Length > 0)
        {
            var steam = Path.GetDirectoryName(steamExe)!;
            var usersPath = Path.Combine(steam, "config", "loginusers.vdf");
            if (File.Exists(usersPath))
            {
                var users = Vdf.Parse(File.ReadAllText(usersPath)).Children.GetValueOrDefault("users");
                if (users is not null)
                    foreach (var (id, user) in users.Children)
                        if (id.Length == 17 && user.Get("AccountName").Length > 0)
                            accounts.Add(new("account" + (accounts.Count + 1), id, user.Get("AccountName"),
                                user.Get("PersonaName", user.Get("AccountName")), "SteamFusion_" + (accounts.Count + 1)));
            }
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
            var libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                var folders = Vdf.Parse(File.ReadAllText(libraryFile)).Children.GetValueOrDefault("libraryfolders");
                if (folders is not null) foreach (var node in folders.Children.Values)
                    if (Path.IsPathFullyQualified(node.Get("path"))) libraries.Add(node.Get("path"));
            }
            foreach (var library in libraries)
            {
                var apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var app = Vdf.Parse(File.ReadAllText(file)).Children.GetValueOrDefault("AppState");
                        if (app is not null && uint.TryParse(app.Get("appid"), out var id))
                            games.Add(new(id, app.Get("name"), Path.Combine(apps, "common", app.Get("installdir")), app.Get("LastOwner")));
                    }
                    catch (IOException) { }
                    catch (InvalidDataException) { }
                }
            }
        }
        return new(steamExe, watt, sandbox, DataDirectory,
            steamExe.Length > 0 && (File.Exists(Path.Combine(Path.GetDirectoryName(steamExe)!, "millennium.dll")) ||
                Directory.Exists(Path.Combine(Path.GetDirectoryName(steamExe)!, "millennium", "bin"))), accounts, games);
    }

    public static Configuration Initial(DiscoveryReport scan)
    {
        var accounts = scan.Accounts.Take(2).ToList();
        var crosscode = scan.Games.FirstOrDefault(g => g.AppId == 368340);
        var candidate = accounts.FirstOrDefault(a => a.SteamId == crosscode?.LastOwner) ?? accounts.FirstOrDefault();
        return new()
        {
            SteamExe = scan.SteamExe, WattExe = scan.WattExe, SandboxieStartExe = scan.SandboxieStartExe,
            Accounts = accounts, DefaultNativeAccountId = accounts.FirstOrDefault()?.Id ?? "",
            Games = candidate is null ? [] : [new(368340, "CrossCode", candidate.Id) { Enabled = false }]
        };
    }
    private static string Existing(string path) => File.Exists(path) ? Path.GetFullPath(path) : "";
}
