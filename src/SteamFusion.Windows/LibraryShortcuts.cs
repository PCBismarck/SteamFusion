using SteamFusion.Core;
using System.Diagnostics;

namespace SteamFusion.Windows;

public static class LibraryShortcuts
{
    public static string Install(Configuration config, string cliExe)
        => Update(config, cliExe, remove: false);
    public static string Remove(Configuration config, string cliExe)
        => Update(config, cliExe, remove: true);
    private static string Update(Configuration config, string cliExe, bool remove)
    {
        config.Validate();
        RequireStopped();
        if (!File.Exists(cliExe)) throw new FileNotFoundException("CLI 不存在。", cliExe);
        var plans = new List<(string Path, byte[] Bytes)>();
        foreach (var user in config.Accounts)
        {
            var userId = ulong.Parse(user.SteamId) - 76561197960265728UL;
            var path = Path.Combine(Path.GetDirectoryName(config.SteamExe)!, "userdata", userId.ToString(), "config", "shortcuts.vdf");
            if (remove && !File.Exists(path)) continue;
            var root = File.Exists(path) ? BinaryVdf.Read(File.ReadAllBytes(path)) : [new BinaryEntry(0, "shortcuts", new List<BinaryEntry>())];
            var entries = root.SingleOrDefault(e => e.Key == "shortcuts" && e.Type == 0)?.Value as List<BinaryEntry>
                ?? throw new InvalidDataException("找不到 shortcuts 根节点。");
            var exe = NativeMethods.Quote(cliExe);
            // A tag alone is not sufficient: never remove another program's shortcuts.
            entries.RemoveAll(e => e.Type == 0 && e.Value is List<BinaryEntry> fields && Text(fields, "exe") == exe &&
                (fields.Any(f => f.Key == "tags" && f.Value is List<BinaryEntry> tags && tags.Any(t => t.Type == 1 && t.Value as string == "SteamFusion")) ||
                System.Text.RegularExpressions.Regex.IsMatch(Text(fields, "LaunchOptions") ?? "", "^launch [1-9][0-9]{0,9} --steamfusion-library$")));
            foreach (var game in config.Games.Where(g => g.Enabled && !remove))
            {
                var account = config.Accounts.Single(a => a.Id == game.AccountId);
                var name = $"{game.Name} · {account.Name}";
                var args = "launch " + game.AppId;
                // Update only our own shortcut identified by exact executable + launch argument.
                var existing = entries.FirstOrDefault(e => e.Type == 0 && e.Value is List<BinaryEntry> fields &&
                    Text(fields, "exe") == exe && Text(fields, "LaunchOptions") == args);
                if (existing is not null) entries.Remove(existing);
                var key = 0; while (entries.Any(e => e.Key == key.ToString())) key++;
                var fields = new List<BinaryEntry>
                {
                    Number("appid", BinaryVdf.ShortcutId(exe, name)), String("AppName", name), String("exe", exe),
                    String("StartDir", NativeMethods.Quote(Path.GetDirectoryName(cliExe)!)), String("icon", ""),
                    String("ShortcutPath", ""), String("LaunchOptions", args), Number("IsHidden", 0),
                    Number("AllowDesktopConfig", 1), Number("AllowOverlay", 0), Number("OpenVR", 0),
                    Number("Devkit", 0), String("DevkitGameID", ""), Number("DevkitOverrideAppID", 0), Number("LastPlayTime", 0),
                    String("FlatpakAppID", ""), new(0, "tags", new List<BinaryEntry> { String("0", "SteamFusion") })
                };
                entries.Add(new(0, key.ToString(), fields));
            }
            // Steam expects a contiguous list of numeric keys.
            for (var index = 0; index < entries.Count; index++) entries[index] = entries[index] with { Key = index.ToString() };
            plans.Add((path, BinaryVdf.Write(root)));
        }
        // Parse every destination before touching any of them. Keep original bytes as timestamped backups.
        var backupStamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
        foreach (var (path, bytes) in plans)
        {
            RequireStopped();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, path + ".steamfusion-" + backupStamp + ".bak", false);
            var temp = path + ".steamfusion.tmp";
            File.WriteAllBytes(temp, bytes); File.Move(temp, path, true);
        }
        return remove ? $"已从 {plans.Count} 个账号移除本目录的 SteamFusion 快捷方式，其他条目与备份已保留。" :
            $"已为 {plans.Count} 个账号更新已启用游戏的库快捷方式。重新打开 Steam，在库中搜索游戏名或 SteamFusion。";
    }
    private static void RequireStopped()
    {
        var processes = Process.GetProcessesByName("steam");
        foreach (var process in processes) process.Dispose();
        if (processes.Length > 0) throw new InvalidOperationException("请先从 Steam 菜单正常退出所有客户端，再更新库快捷方式。");
    }
    private static string? Text(List<BinaryEntry> fields, string key) => fields.FirstOrDefault(e => e.Type == 1 && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value as string;
    private static BinaryEntry String(string name, string value) => new(1, name, value);
    private static BinaryEntry Number(string name, uint value) => new(2, name, BitConverter.GetBytes(value));
}
