using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SteamFusion.Core;

public sealed record GameDataSource(string Environment, string UserConfigDirectory);
public sealed record ExportAchievement(string ApiName, string Name, string Description, bool? Unlocked,
    DateTimeOffset? UnlockedAtUtc, bool? Hidden, double? Progress, double? ProgressMax);
public sealed record ExportGame(uint AppId, string Name)
{
    public long? PlaytimeMinutes { get; set; }
    public decimal? PlaytimeHours => PlaytimeMinutes is long value ? Math.Round(value / 60m, 2) : null;
    public DateTimeOffset? LastPlayedAtUtc { get; set; }
    public string? PlaytimeSource { get; set; }
    public DateTimeOffset? PlaytimeFileUpdatedAtUtc { get; set; }
    public int? AchievementTotal { get; set; }
    public int? AchievementUnlocked { get; set; }
    public decimal? AchievementPercent => AchievementTotal is > 0 && AchievementUnlocked is int unlocked ? Math.Round(unlocked * 100m / AchievementTotal.Value, 2) : null;
    public string? AchievementSummarySource { get; set; }
    public DateTimeOffset? AchievementSummaryAtUtc { get; set; }
    public string? AchievementDetailsSource { get; set; }
    public DateTimeOffset? AchievementDetailsFileUpdatedAtUtc { get; set; }
    public string DetailCoverage { get; set; } = "not-cached";
    public List<ExportAchievement> Achievements { get; set; } = [];
}
public sealed record ExportAccount(string AccountId, string SteamId, string Name, List<GameDataSource> Sources,
    List<ExportGame> Games, List<string> Warnings);
public sealed record GameDataReport(int SchemaVersion, DateTimeOffset ExportedAtUtc, string Mode,
    bool IncludesAchievementDetails, List<ExportAccount> Accounts)
{
    public List<string> Notes { get; init; } = [
        "本报告为本机 Steam 缓存快照，不代表已实时查询服务器。",
        "按 SteamID 分开记录；同一账号的普通与沙盒副本按字段选较新记录，不叠加时长。",
        "文件更新时间不等于服务器同步时间。空值表示未知，不能视为零；缓存可能只包含部分成就。",
        "成就明细 complete-cache 只表示条目数、解锁数与选中的缓存汇总一致，不保证服务器数据仍然相同。",
        "本机缓存与路由清单中的游戏条目不作为游戏购买或许可证明。",
        "零项占位缓存不覆盖已有明确成就记录；名称未知的历史条目保留 AppID。",
        "CSV 使用 UTF-8 BOM；SteamID 前置单引号以避免 Excel 丢失数字精度，JSON 保留原始字符串。"
    ];
}

public static class GameDataReader
{
    private const long MaxFileBytes = 16 * 1024 * 1024;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ExportAccount Read(Account account, IEnumerable<GameDataSource> sources,
        IReadOnlyDictionary<uint, string> names, IEnumerable<uint> catalogAppIds, CancellationToken ct = default)
    {
        var roots = sources.DistinctBy(s => s.UserConfigDirectory, StringComparer.OrdinalIgnoreCase).ToList();
        var games = new Dictionary<uint, ExportGame>(); var warnings = new List<string>();
        ExportGame Game(uint id)
        {
            if (!games.TryGetValue(id, out var game)) games[id] = game = new(id, names.GetValueOrDefault(id, "App " + id));
            return game;
        }
        foreach (var id in catalogAppIds.Where(id => id > 0 && id < 0x80000000 && id != 228980).Distinct()) Game(id);
        var summaries = new Dictionary<uint, List<(int Total, int Unlocked, DateTimeOffset Time, string Source)>>();
        var details = new Dictionary<uint, List<(int? Total, int? Unlocked, List<ExportAchievement> Items, DateTimeOffset Time, string Source)>>();
        void Summary(uint id, int total, int unlocked, DateTimeOffset time, string source)
        {
            if (total <= 0 || unlocked < 0 || unlocked > total) return;
            if (!summaries.TryGetValue(id, out var list)) summaries[id] = list = [];
            list.Add((total, unlocked, time, source));
        }
        foreach (var source in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.UserConfigDirectory)) continue;
            var local = Path.Combine(source.UserConfigDirectory, "localconfig.vdf");
            ReadFile(local, warnings, text =>
            {
                var apps = Vdf.Parse(text);
                foreach (var key in new[] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" })
                {
                    if (!apps.Children.TryGetValue(key, out var child)) throw new InvalidDataException("缺少时长数据节点 " + key);
                    apps = child;
                }
                var time = Updated(local);
                foreach (var (key, value) in apps.Children)
                {
                    if (!AppId(key, out var id)) continue;
                    var minutes = Long(value.Get("Playtime")); var last = Unix(Long(value.Get("LastPlayed")));
                    if (minutes is null && last is null) continue;
                    var game = Game(id);
                    // File copies are alternative snapshots of the same Steam account, never additive.
                    if (minutes is not null && (game.PlaytimeFileUpdatedAtUtc is null || time > game.PlaytimeFileUpdatedAtUtc))
                    {
                        game.PlaytimeMinutes = minutes; game.PlaytimeSource = source.Environment + "/localconfig.vdf";
                        game.PlaytimeFileUpdatedAtUtc = time; game.LastPlayedAtUtc = last;
                    }
                    else if (game.PlaytimeMinutes is null && (game.LastPlayedAtUtc is null || last > game.LastPlayedAtUtc)) game.LastPlayedAtUtc = last;
                }
            });
            var cache = Path.Combine(source.UserConfigDirectory, "librarycache");
            var progress = Path.Combine(cache, "achievement_progress.json");
            ReadFile(progress, warnings, text =>
            {
                using var document = JsonDocument.Parse(text); var root = document.RootElement;
                if (!root.TryGetProperty("nVersion", out var version) || version.GetInt32() != 3 ||
                    !root.TryGetProperty("mapCache", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("成就进度缓存格式变化");
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 2 || !entry[0].TryGetUInt32(out var id) || !AppId(id.ToString(Invariant), out _)) continue;
                    var value = entry[1]; var total = Int(value, "total"); var unlocked = Int(value, "unlocked");
                    if (total is null || unlocked is null || unlocked > total) continue;
                    Game(id);
                    var time = Unix(NumberLong(value, "cache_time")) ?? Updated(progress);
                    Summary(id, total.Value, unlocked.Value, time, source.Environment + "/achievement_progress.json");
                }
            });
            if (!Directory.Exists(cache)) continue;
            string[] files;
            try { files = Directory.GetFiles(cache, "*.json"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add(source.Environment + "：无法读取成就缓存目录。"); continue; }
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!AppId(Path.GetFileNameWithoutExtension(file), out var id)) continue;
                ReadFile(file, warnings, text =>
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("成就缓存格式变化");
                    foreach (var section in document.RootElement.EnumerateArray())
                    {
                        if (section.ValueKind != JsonValueKind.Array || section.GetArrayLength() != 2 || section[0].GetString() != "achievements") continue;
                        var envelope = section[1];
                        if (!envelope.TryGetProperty("version", out var version) || version.GetInt32() != 2 ||
                            !envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("成就缓存版本不支持");
                        var total = Int(data, "nTotal"); var unlocked = Int(data, "nAchieved");
                        if (total is null || unlocked is null || unlocked > total) throw new InvalidDataException("成就汇总数值无效");
                        var items = new Dictionary<string, ExportAchievement>(StringComparer.Ordinal);
                        foreach (var vector in new[] { "vecHighlight", "vecUnachieved", "vecAchievedHidden" })
                        {
                            if (!data.TryGetProperty(vector, out var array) || array.ValueKind != JsonValueKind.Array) continue;
                            foreach (var item in array.EnumerateArray())
                            {
                                var key = Text(item, "strID"); if (string.IsNullOrWhiteSpace(key)) continue;
                                var achieved = Bool(item, "bAchieved");
                                var row = new ExportAchievement(key, Text(item, "strName"), Text(item, "strDescription"), achieved,
                                    achieved == true ? Unix(NumberLong(item, "rtUnlocked")) : null,
                                    vector == "vecAchievedHidden" ? true : Bool(item, "bHidden"), Double(item, "flCurrentProgress"), Double(item, "flMaxProgress"));
                                if (items.TryGetValue(key, out var previous))
                                {
                                    if (previous.Unlocked != row.Unlocked) throw new InvalidDataException("同一成就的缓存状态冲突");
                                    items[key] = previous with { Hidden = previous.Hidden == true || row.Hidden == true ? true : previous.Hidden ?? row.Hidden };
                                }
                                else items.Add(key, row);
                            }
                        }
                        var time = Updated(file); var label = source.Environment + "/librarycache/" + id + ".json";
                        Game(id); Summary(id, total.Value, unlocked.Value, time, label);
                        if (!details.TryGetValue(id, out var list)) details[id] = list = [];
                        list.Add((total, unlocked, items.Values.OrderBy(a => a.ApiName, StringComparer.Ordinal).ToList(), time, label));
                    }
                });
            }
        }
        foreach (var (id, game) in games)
        {
            if (summaries.TryGetValue(id, out var counts))
            {
                var latest = counts.OrderByDescending(c => c.Time).First();
                game.AchievementTotal = latest.Total; game.AchievementUnlocked = latest.Unlocked;
                game.AchievementSummaryAtUtc = latest.Time; game.AchievementSummarySource = latest.Source;
            }
            if (details.TryGetValue(id, out var entries))
            {
                // Newer zero-item placeholders do not erase an older, explicitly populated record.
                var populated = entries.Where(d => d.Total > 0 || d.Items.Count > 0).ToList();
                var latest = (populated.Count > 0 ? populated : entries).OrderByDescending(d => d.Time).First();
                game.Achievements = latest.Items; game.AchievementDetailsSource = latest.Source;
                game.AchievementDetailsFileUpdatedAtUtc = latest.Time;
                game.DetailCoverage = latest.Total == 0 ? "unverified-zero" : "partial-cache";
                if (game.AchievementTotal is > 0 && game.AchievementTotal == latest.Total && game.AchievementUnlocked == latest.Unlocked &&
                    latest.Items.Count == game.AchievementTotal && latest.Items.All(a => a.Unlocked is not null) &&
                    latest.Items.Count(a => a.Unlocked == true) == game.AchievementUnlocked) game.DetailCoverage = "complete-cache";
                if (latest.Items.Count > latest.Total || latest.Items.Count(a => a.Unlocked == true) > latest.Unlocked ||
                    (game.AchievementTotal is int total && (latest.Total != total || latest.Unlocked != game.AchievementUnlocked))) game.DetailCoverage = "inconsistent-cache";
            }
        }
        if (!roots.Any(s => Directory.Exists(s.UserConfigDirectory))) warnings.Add("未找到此账号的普通或沙盒 Steam 用户数据目录。");
        return new(account.Id, account.SteamId, account.Name, roots, games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings);
    }
    private static bool AppId(string value, out uint id) => uint.TryParse(value, NumberStyles.None, Invariant, out id) && id > 0 && id < 0x80000000 && id != 228980;
    private static long? Long(string value) => long.TryParse(value, NumberStyles.None, Invariant, out var result) && result >= 0 ? result : null;
    private static long? NumberLong(JsonElement value, string key) => value.TryGetProperty(key, out var node) && node.TryGetInt64(out var result) && result >= 0 ? result : null;
    private static int? Int(JsonElement value, string key) => NumberLong(value, key) is long n && n <= int.MaxValue ? (int)n : null;
    private static double? Double(JsonElement value, string key) => value.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;
    private static bool? Bool(JsonElement value, string key) => value.TryGetProperty(key, out var node) && node.ValueKind is JsonValueKind.True or JsonValueKind.False ? node.GetBoolean() : null;
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : "";
    private static DateTimeOffset? Unix(long? seconds)
    { try { return seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null; } catch (ArgumentOutOfRangeException) { return null; } }
    private static DateTimeOffset Updated(string path) => new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    private static void ReadFile(string path, List<string> warnings, Action<string> parse)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes) throw new InvalidDataException("文件超过大小限制");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { warnings.Add(Path.GetFileName(path) + "：" + ex.GetType().Name + "，此缓存未完整读取。"); }
    }
}

public static class GameDataWriter
{
    public static string Write(GameDataReport report, string destination, CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination); Directory.CreateDirectory(destination);
        var name = "SteamFusion-" + report.ExportedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var staging = Path.Combine(destination, ".export-" + Guid.NewGuid().ToString("N")); var final = Path.Combine(destination, name);
        Directory.CreateDirectory(staging);
        try
        {
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(staging, "data.json"), Json.Encode(report), new UTF8Encoding(false));
            using (var writer = new StreamWriter(Path.Combine(staging, "games.csv"), false, new UTF8Encoding(true)))
            {
                Row(writer, "账号", "SteamID", "AppID", "游戏", "时长_分钟", "时长_小时", "最后游玩_UTC", "时长来源", "时长文件更新时间_UTC", "成就已解锁", "成就总数", "成就完成率_百分比", "成就汇总来源", "成就汇总记录时间_UTC", "导出明细条数", "明细完整性", "成就明细来源", "成就明细文件更新时间_UTC");
                foreach (var account in report.Accounts) foreach (var game in account.Games)
                {
                    ct.ThrowIfCancellationRequested();
                    Row(writer, account.Name, "'" + account.SteamId, game.AppId, game.Name, game.PlaytimeMinutes, game.PlaytimeHours, game.LastPlayedAtUtc,
                        game.PlaytimeSource, game.PlaytimeFileUpdatedAtUtc, game.AchievementUnlocked, game.AchievementTotal, game.AchievementPercent,
                        game.AchievementSummarySource, game.AchievementSummaryAtUtc, game.Achievements.Count, Coverage(game.DetailCoverage), game.AchievementDetailsSource, game.AchievementDetailsFileUpdatedAtUtc);
                }
            }
            if (report.IncludesAchievementDetails)
                using (var writer = new StreamWriter(Path.Combine(staging, "achievements.csv"), false, new UTF8Encoding(true)))
                {
                    Row(writer, "账号", "SteamID", "AppID", "游戏", "成就ID", "成就名称", "说明", "已解锁", "解锁时间_UTC", "隐藏成就", "当前进度", "目标进度", "数据来源", "文件更新时间_UTC", "明细完整性");
                    foreach (var account in report.Accounts) foreach (var game in account.Games) foreach (var achievement in game.Achievements)
                    {
                        ct.ThrowIfCancellationRequested();
                        Row(writer, account.Name, "'" + account.SteamId, game.AppId, game.Name, achievement.ApiName, achievement.Name, achievement.Description,
                            achievement.Unlocked, achievement.UnlockedAtUtc, achievement.Hidden, achievement.Progress, achievement.ProgressMax, game.AchievementDetailsSource, game.AchievementDetailsFileUpdatedAtUtc, Coverage(game.DetailCoverage));
                    }
                }
            File.WriteAllText(Path.Combine(staging, "README.txt"), "SteamFusion 游戏数据导出\r\n\r\n" + string.Join("\r\n", report.Notes) +
                "\r\n\r\n明细状态：complete-cache=缓存明细齐全；partial-cache=部分明细；not-cached=未缓存；unverified-zero=缓存报告零项、支持情况未确认；inconsistent-cache=汇总与明细不一致。\r\n\r\n" +
                string.Join("\r\n", report.Accounts.SelectMany(a => a.Warnings.Select(w => a.Name + "：" + w))), new UTF8Encoding(true));
            ct.ThrowIfCancellationRequested(); Directory.Move(staging, final); return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static string Coverage(string value) => value switch
    { "complete-cache" => "缓存明细齐全", "partial-cache" => "部分明细", "unverified-zero" => "零项未确认", "inconsistent-cache" => "缓存不一致", _ => "未缓存" };
    private static void Row(TextWriter writer, params object?[] values) => writer.WriteLine(string.Join(',', values.Select(Cell)));
    public static string Cell(object? value)
    {
        var text = value switch { null => "", DateTimeOffset date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), bool boolean => boolean ? "是" : "否", IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        if (value is string && text.TrimStart() is { Length: > 0 } trimmed && "=+-@".Contains(trimmed[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
