using System.Text;
using SteamFusion.Core;

internal static class GameDataExportTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Export chooses the newer account snapshot without adding sandbox playtime", () =>
        {
            using var f = new Fixture(); f.Playtime("native", 60, -2); f.Playtime("box", 90, -1);
            var game = f.Read().Games.Single(g => g.AppId == 10);
            Assert(game.PlaytimeMinutes == 90 && game.PlaytimeHours == 1.5m && game.PlaytimeSource!.StartsWith("box/"));
            Assert(game.LastPlayedAtUtc == DateTimeOffset.FromUnixTimeSeconds(1700000000));
        });
        test("Export keeps unknown playtime empty and preserves explicit zero", () =>
        {
            using var f = new Fixture(); f.Playtime("native", 0, -1);
            var games = f.Read().Games; Assert(games.Single(g => g.AppId == 10).PlaytimeMinutes == 0);
            Assert(games.Single(g => g.AppId == 20).PlaytimeMinutes is null);
        });
        test("Export deduplicates hidden achievement highlights and marks partial detail coverage", () =>
        {
            using var f = new Fixture(); f.Details("native", 3, 1, true, -1);
            var game = f.Read().Games.Single(g => g.AppId == 10);
            Assert(game.Achievements.Count == 2 && game.DetailCoverage == "partial-cache");
            Assert(game.Achievements.Single(a => a.ApiName == "FIRST").Hidden == true);
            Assert(game.Achievements.Single(a => a.ApiName == "FIRST").UnlockedAtUtc == DateTimeOffset.FromUnixTimeSeconds(1700000000));
            Assert(game.Achievements.Single(a => a.ApiName == "SECOND").UnlockedAtUtc is null);
        });
        test("Export recognizes complete cached detail lists without inventing hidden records", () =>
        {
            using var f = new Fixture(); f.Details("native", 2, 1, true, -1);
            var game = f.Read().Games.Single(g => g.AppId == 10);
            Assert(game.DetailCoverage == "complete-cache" && game.AchievementPercent == 50);
        });
        test("Export flags summary and detail disagreements instead of reporting complete", () =>
        {
            using var f = new Fixture(); f.Details("native", 2, 1, true, -2);
            var cache = Path.Combine(f.Root("native"), "librarycache", "achievement_progress.json");
            File.WriteAllText(cache, "{\"nVersion\":3,\"mapCache\":[[10,{\"total\":3,\"unlocked\":2,\"cache_time\":1800000000}]]}");
            var game = f.Read().Games.Single(g => g.AppId == 10);
            Assert(game.DetailCoverage == "inconsistent-cache" && game.AchievementTotal == 3 && game.AchievementUnlocked == 2);
        });
        test("Zero-item achievement placeholders are unknown rather than proof of no achievements", () =>
        {
            using var f = new Fixture(); f.Details("native", 0, 0, false, -1);
            var game = f.Read().Games.Single(g => g.AppId == 10);
            Assert(game.AchievementTotal is null && game.AchievementUnlocked is null && game.DetailCoverage == "unverified-zero");
        });
        test("Newer zero placeholders do not erase older populated achievement records", () =>
        {
            using var f = new Fixture(); f.Details("native", 2, 1, true, -2); f.Details("box", 0, 0, false, -1);
            var game = f.Read().Games.Single(g => g.AppId == 10);
            Assert(game.Achievements.Count == 2 && game.DetailCoverage == "complete-cache" && game.AchievementDetailsSource!.StartsWith("native/"));
        });
        test("Broken cache files are reported and do not prevent other games from exporting", () =>
        {
            using var f = new Fixture(); f.Playtime("native", 15, -1);
            Directory.CreateDirectory(Path.Combine(f.Root("native"), "librarycache"));
            File.WriteAllText(Path.Combine(f.Root("native"), "librarycache", "10.json"), "not json");
            var report = f.Read(); Assert(report.Warnings.Count == 1 && report.Games.Single(g => g.AppId == 10).PlaytimeMinutes == 15);
        });
        test("CSV neutralizes formulas and escapes commas quotes and multiline Chinese", () =>
        {
            Assert(GameDataWriter.Cell(" =HYPERLINK(\"x\")").StartsWith("\"' =HYPERLINK"));
            Assert(GameDataWriter.Cell("\t@SUM(1)").StartsWith("\"'\t@SUM"));
            Assert(GameDataWriter.Cell("中文,\"标题\"\n第二行") == "\"中文,\"\"标题\"\"\n第二行\"");
            Assert(GameDataWriter.Cell(null) == "\"\"");
        });
        test("Export writes BOM CSV plus typed JSON and never copies unrelated login fields", () =>
        {
            using var f = new Fixture(); f.Playtime("native", 75, -1); f.Details("native", 2, 1, true, -1);
            var report = new GameDataReport(1, DateTimeOffset.UtcNow, "local-cache", true, [f.Read()]);
            var directory = GameDataWriter.Write(report, Path.Combine(f.DirectoryPath, "output"));
            Assert(Directory.GetFiles(directory).Length == 4);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "games.csv")); Assert(bytes[..3].SequenceEqual(Encoding.UTF8.GetPreamble()));
            var json = File.ReadAllText(Path.Combine(directory, "data.json")); Assert(!json.Contains("DO_NOT_EXPORT_SECRET"));
            var restored = Json.Decode<GameDataReport>(json); Assert(restored.Accounts.Single().Games.Single(g => g.AppId == 10).PlaytimeMinutes == 75);
            var csv = File.ReadAllText(Path.Combine(directory, "achievements.csv")); Assert(csv.Contains("'76561198000000001") && csv.Contains("第一步"));
        });
        test("Separate Steam accounts never merge their playtime or achievements", () =>
        {
            using var f = new Fixture(); f.Playtime("native", 60, -2); f.Playtime("other", 120, -1);
            var first = GameDataReader.Read(new("a", "76561198000000001", "a", "A", "BoxA"), [new("native", f.Root("native"))], new Dictionary<uint, string>(), [10]);
            var second = GameDataReader.Read(new("b", "76561198000000002", "b", "B", "BoxB"), [new("box", f.Root("other"))], new Dictionary<uint, string>(), [10]);
            Assert(first.Games.Single().PlaytimeMinutes == 60 && second.Games.Single().PlaytimeMinutes == 120);
            Assert(first.SteamId != second.SteamId);
        });
        test("Cancellation removes staging files and repeated exports do not overwrite", () =>
        {
            using var f = new Fixture(); var report = new GameDataReport(1, DateTimeOffset.UtcNow, "local-cache", true, [f.Read()]);
            var destination = Path.Combine(f.DirectoryPath, "output"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { GameDataWriter.Write(report, destination, cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Assert(!Directory.EnumerateFileSystemEntries(destination).Any());
            var one = GameDataWriter.Write(report, destination); var two = GameDataWriter.Write(report, destination);
            Assert(one != two && Directory.Exists(one) && Directory.Exists(two));
        });
        test("Unsupported cache versions fail visibly without fabricating achievement counts", () =>
        {
            using var f = new Fixture(); f.Details("native", 2, 1, true, -1);
            var file = Path.Combine(f.Root("native"), "librarycache", "10.json");
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"version\":2", "\"version\":99"));
            var report = f.Read(); Assert(report.Warnings.Count == 1 && report.Games.All(g => g.AchievementTotal is null));
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Export assertion failed"); }
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "SteamFusion-export-tests-" + Guid.NewGuid().ToString("N"));
        public string Root(string environment) => Path.Combine(DirectoryPath, environment);
        public void Playtime(string environment, int minutes, int age)
        {
            Directory.CreateDirectory(Root(environment)); var file = Path.Combine(Root(environment), "localconfig.vdf");
            File.WriteAllText(file, "\"UserLocalConfigStore\" { \"LoginSecret\" \"DO_NOT_EXPORT_SECRET\" \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"10\" { \"Playtime\" \"" + minutes + "\" \"LastPlayed\" \"1700000000\" } } } } } }");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(age));
        }
        public void Details(string environment, int total, int unlocked, bool items, int age)
        {
            var root = Path.Combine(Root(environment), "librarycache"); Directory.CreateDirectory(root);
            var first = "{\"strID\":\"FIRST\",\"strName\":\"第一步\",\"strDescription\":\"Hello, world\",\"bAchieved\":true,\"rtUnlocked\":1700000000}";
            var second = "{\"strID\":\"SECOND\",\"strName\":\"未完成\",\"bAchieved\":false,\"rtUnlocked\":0}";
            File.WriteAllText(Path.Combine(root, "10.json"), "[[\"achievements\",{\"version\":2,\"data\":{\"nTotal\":" + total + ",\"nAchieved\":" + unlocked + ",\"vecHighlight\":[" + (items ? first : "") + "],\"vecUnachieved\":[" + (items ? second : "") + "],\"vecAchievedHidden\":[" + (items ? first : "") + "]}}]]");
            File.SetLastWriteTimeUtc(Path.Combine(root, "10.json"), DateTime.UtcNow.AddHours(age));
        }
        public ExportAccount Read() => GameDataReader.Read(new("a", "76561198000000001", "a", "A", "BoxA"),
            [new("native", Root("native")), new("box", Root("box"))], new Dictionary<uint, string> { [10] = "游戏", [20] = "未缓存游戏" }, [10, 20]);
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
}
