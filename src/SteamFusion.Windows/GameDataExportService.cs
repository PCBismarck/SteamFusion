using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SteamFusion.Core;

namespace SteamFusion.Windows;

public static class GameDataExportService
{
    public static GameDataReport Read(Configuration config, string? accountId = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        config.Validate(); NativeMethods.RequireOutsideSandbox();
        var accounts = config.Accounts.Where(a => accountId is null || a.Id == accountId).ToList();
        if (accounts.Count == 0) throw new InvalidOperationException("没有可导出的账号。");
        var steamRoot = Path.GetDirectoryName(config.SteamExe);
        if (string.IsNullOrWhiteSpace(steamRoot) || !Path.IsPathFullyQualified(steamRoot)) throw new InvalidOperationException("请先设置 Steam 路径。");
        var names = config.Games.ToDictionary(g => g.AppId, g => g.Name);
        foreach (var game in Discovery.Scan().Games) names.TryAdd(game.AppId, game.Name);
        var boxes = new List<(string Name, string Root)>(); var rootWarnings = new List<string>();
        foreach (var account in config.Accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = SandboxDataPath.GetRoot(config.SandboxieStartExe, account.SandboxName);
                if (root is not null) boxes.Add((account.SandboxName, root));
                else if (config.SandboxieStartExe.Length > 0) rootWarnings.Add(account.SandboxName + "：无法定位沙盒目录，只使用其他可读取的副本。");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { rootWarnings.Add(account.SandboxName + "：未读取沙盒目录（" + ex.GetType().Name + "）。"); }
        }
        var results = new List<ExportAccount>();
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested(); progress?.Report("正在读取 " + account.Name + " 的时长与成就…");
            var ids = config.Games.Where(g => g.AccountId == account.Id).Select(g => g.AppId).ToHashSet();
            var warnings = new List<string>(rootWarnings);
            var catalog = Path.Combine(Discovery.DataDirectory, "catalogs", account.SteamId + ".json");
            if (File.Exists(catalog))
            {
                try
                {
                    if (new FileInfo(catalog).Length > 4 * 1024 * 1024) throw new InvalidDataException("快照过大");
                    var snapshot = Json.Decode<LibrarySnapshot>(File.ReadAllText(catalog)); snapshot.Validate();
                    if (snapshot.SteamId != account.SteamId) throw new InvalidDataException("账号不匹配");
                    foreach (var game in snapshot.Games) { ids.Add(game.AppId); names.TryAdd(game.AppId, game.Name); }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { warnings.Add("此账号的游戏库清单未读取，使用可用的路由和本机缓存条目。"); }
            }
            var userId = (ulong.Parse(account.SteamId, CultureInfo.InvariantCulture) - 76561197960265728UL).ToString(CultureInfo.InvariantCulture);
            var userConfig = Path.Combine(steamRoot, "userdata", userId, "config");
            var sources = new List<GameDataSource> { new("native", userConfig) };
            foreach (var (name, root) in boxes)
            {
                // Sandboxie drive mirrors preserve the full per-SteamID userdata path.
                if (userConfig.Length < 3 || userConfig[1] != ':') { warnings.Add("Steam 使用非盘符路径，沙盒副本未读取。"); continue; }
                var shadow = Path.Combine(root, "drive", userConfig[..1], userConfig[3..]);
                sources.Add(new(name, shadow));
            }
            var result = GameDataReader.Read(account, sources, names, ids, ct);
            result.Warnings.AddRange(warnings); results.Add(result);
        }
        return new(1, DateTimeOffset.UtcNow, "local-cache", true, results);
    }
}

internal static class SandboxDataPath
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryBoxPath(string box, StringBuilder file, IntPtr key, IntPtr ipc, ref uint fileBytes, IntPtr keyBytes, IntPtr ipcBytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int length);
    public static string? GetRoot(string startExe, string box)
    {
        var dll = Path.Combine(Path.GetDirectoryName(startExe) ?? "", "SbieDll.dll");
        if (!Path.IsPathFullyQualified(dll) || !File.Exists(dll)) return null;
        var library = NativeLibrary.Load(dll);
        try
        {
            var query = Marshal.GetDelegateForFunctionPointer<QueryBoxPath>(NativeLibrary.GetExport(library, "SbieApi_QueryBoxPath"));
            var buffer = new StringBuilder(16384); uint bytes = 32768;
            if (query(box, buffer, IntPtr.Zero, IntPtr.Zero, ref bytes, IntPtr.Zero, IntPtr.Zero) != 0) return null;
            var path = buffer.ToString();
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return path[4..];
            foreach (var drive in DriveInfo.GetDrives())
            {
                var target = new StringBuilder(4096);
                if (QueryDosDevice(drive.Name[..2], target, target.Capacity) == 0) continue;
                var device = target.ToString();
                if (path.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive.Name[..2] + path[device.Length..];
            }
            return Path.IsPathFullyQualified(path) && path.Length > 1 && path[1] == ':' ? path : null;
        }
        finally { NativeLibrary.Free(library); }
    }
}
