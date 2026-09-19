using SteamFusion.Core;

namespace SteamFusion.Windows;

/// <summary>Read current manifests, not the historical installation flag in a library snapshot.</summary>
public static class InstallationIndex
{
    public static void Refresh(Configuration config)
    {
        config.Validate(); NativeMethods.RequireOutsideSandbox();
        var installed = new HashSet<uint>(); var warnings = new List<string>();
        var steam = Path.GetDirectoryName(config.SteamExe);
        if (string.IsNullOrWhiteSpace(steam) || !Path.IsPathFullyQualified(steam)) throw new InvalidDataException("Steam 路径无效。");
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        void ReadLibraries(string file)
        {
            if (!File.Exists(file)) return;
            var folders = Vdf.Parse(File.ReadAllText(file)).Children.GetValueOrDefault("libraryfolders");
            if (folders is not null) foreach (var item in folders.Children.Values)
                if (Path.IsPathFullyQualified(item.Get("path"))) libraries.Add(item.Get("path"));
        }
        try
        {
            ReadLibraries(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            var roots = config.Accounts.Select(a => SandboxDataPath.GetRoot(config.SandboxieStartExe, a.SandboxName)).ToArray();
            if (roots.Any(r => r is null) && config.SandboxieStartExe.Length > 0)
                warnings.Add("部分沙盒目录无法定位，暂缓未安装游戏页更新。");
            string? Shadow(string root, string path) => path.Length > 3 && path[1] == ':'
                ? Path.Combine(root, "drive", path[..1], path[3..]) : null;
            foreach (var root in roots.OfType<string>())
                if (Shadow(root, Path.Combine(steam, "steamapps", "libraryfolders.vdf")) is { } file) ReadLibraries(file);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                directories.Add(Path.Combine(library, "steamapps"));
                foreach (var root in roots.OfType<string>())
                    if (Shadow(root, Path.Combine(library, "steamapps")) is { } shadow) directories.Add(shadow);
            }
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "appmanifest_*.acf"))
                {
                    try
                    {
                        var app = Vdf.Parse(File.ReadAllText(file)).Children.GetValueOrDefault("AppState");
                        if (app is not null && uint.TryParse(app.Get("appid"), out var id) &&
                            uint.TryParse(app.Get("StateFlags"), out var flags) && (flags & 4) != 0)
                        {
                            // An empty, abandoned or partially downloaded manifest is not a completed install.
                            var name = app.Get("installdir");
                            if (name.Length > 0 && !name.Contains("..") && name.IndexOfAny(['\\', '/', ':']) < 0 &&
                                Directory.Exists(Path.Combine(directory, "common", name))) installed.Add(id);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    { warnings.Add("安装清单正在变化，稍后重试。"); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { warnings.Add("安装目录读取失败，暂缓未安装游戏页更新。"); }
        ConfigurationStore.AtomicWrite(Path.Combine(Discovery.DataDirectory, "installation-index.json"), Json.Encode(new
        {
            schemaVersion = 1, capturedAt = DateTimeOffset.UtcNow, complete = warnings.Count == 0,
            accounts = config.Accounts.Select(a => a.SteamId).Order().ToArray(),
            installed = installed.Order().ToArray(), warnings = warnings.Distinct().ToArray()
        }));
    }
}
