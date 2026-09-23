using SteamFusion.Core;
using SteamFusion.Windows;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("SteamFusion 运行目标为 Windows。"); return 2; }
try
{
    var command = args.FirstOrDefault() ?? "help";
    switch (command)
    {
        case "discover":
            Console.WriteLine(Json.Encode(Discovery.Scan())); return 0;
        case "init":
            if (File.Exists(Discovery.Store.ConfigPath)) throw new InvalidOperationException("配置已存在，未覆盖。请使用设置窗口修改。");
            var initial = Discovery.Initial(Discovery.Scan());
            Discovery.Store.Save(initial); Discovery.WritePluginConfiguration(initial);
            Console.WriteLine("已创建初始配置；游戏映射需在设置中核对后启用。"); return 0;
        case "probe":
            var runtime = new WindowsRuntime(new NoInteraction());
            Console.WriteLine(Json.Encode(await runtime.ObserveAsync(Discovery.Store.Load(), CancellationToken.None))); return 0;
        case "plan":
            if (args.Length != 2 || !uint.TryParse(args[1], out var planId)) throw new ArgumentException("plan <AppID>");
            var config = Discovery.Store.Load();
            var snap = await new WindowsRuntime(new NoInteraction()).ObserveAsync(config, CancellationToken.None);
            var game = config.Games.Single(g => g.AppId == planId);
            Console.WriteLine(Json.Encode(RoutePlanner.Plan(config, game, snap))); return 0;
        case "launch":
            if ((args.Length != 2 && !(args.Length == 3 && args[2] == "--steamfusion-library")) || !uint.TryParse(args[1], out var appId) || appId == 0) throw new ArgumentException("launch <AppID>");
            return await Send(new("launch", AppId: appId, RequestId: Guid.NewGuid()));
        case "swap":
            if (args.Length != 2) throw new ArgumentException("swap <配置中的账号 ID>");
            return await Send(new("swap", AccountId: args[1], RequestId: Guid.NewGuid()));
        case "launch-as":
            if (args.Length != 3 || !uint.TryParse(args[1], out var chosenApp) || chosenApp == 0 || !System.Text.RegularExpressions.Regex.IsMatch(args[2], "^765[0-9]{14}$"))
                throw new ArgumentException("launch-as <AppID> <SteamID>");
            return await Send(new("launch-as", AppId: chosenApp, AccountId: args[2], RequestId: Guid.NewGuid()));
        case "download":
            if (args.Length != 2 || !uint.TryParse(args[1], out var downloadId) || downloadId == 0) throw new ArgumentException("download <AppID>");
            return await Send(new("download", AppId: downloadId, RequestId: Guid.NewGuid()));
        case "export-data":
            if (args.Length is < 2 or > 3) throw new ArgumentException("export-data <输出目录> [账号 ID]");
            var exportReport = GameDataExportService.Read(Discovery.Store.Load(), args.Length == 3 ? args[2] : null);
            var exportPath = GameDataWriter.Write(exportReport, args[1]);
            Console.WriteLine(Json.Encode(new { directory = exportPath, accounts = exportReport.Accounts.Select(a => new { a.AccountId, games = a.Games.Count,
                playtimeKnown = a.Games.Count(g => g.PlaytimeMinutes.HasValue), achievements = a.Games.Sum(g => g.Achievements.Count), warnings = a.Warnings.Count }) })); return 0;
        case "catalog-preview": Console.WriteLine(Json.Encode(CatalogStore.Preview(Discovery.Store.Load()))); return 0;
        case "catalog-import":
            if (args.Length == 1) return await Send(new(command));
            if (args.Length == 2 && uint.TryParse(args[1], out var importId) && importId > 0) return await Send(new(command, AppId: importId));
            throw new ArgumentException("catalog-import [AppID]");
        case "status": case "settings": case "cancel":
            return await Send(new(command));
        case "exit":
            try { var reply = await Ipc.SendAsync(new("exit"), false, CancellationToken.None); Console.WriteLine(Json.Encode(reply)); return reply.Success ? 0 : 1; }
            catch (TimeoutException) { Console.WriteLine("后台未运行。"); return 0; }
        case "install-shortcuts":
            Console.WriteLine(LibraryShortcuts.Install(Discovery.Store.Load(), Environment.ProcessPath!)); return 0;
        case "remove-shortcuts":
            Console.WriteLine(LibraryShortcuts.Remove(Discovery.Store.Load(), Environment.ProcessPath!)); return 0;
        case "pipe-name": Console.WriteLine(Ipc.PipeName); return 0;
        case "refresh-installations": InstallationIndex.Refresh(Discovery.Store.Load()); return 0;
        case "refresh-launch-options": LaunchOptionsStore.Refresh(Discovery.Store.Load()); return 0;
        case "refresh-plugin": Discovery.WritePluginConfiguration(Discovery.Store.Load()); return 0;
        default:
            Console.WriteLine("SteamFusion.Cli discover | init | probe | plan <AppID> | launch <AppID> | download <AppID> | swap <accountId> | status | settings | cancel | exit | catalog-preview | catalog-import | install-shortcuts | remove-shortcuts | pipe-name | refresh-plugin | export-data <directory> [accountId]"); return command == "help" ? 0 : 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    if (args.FirstOrDefault() is "launch" or "swap" or "download" or "settings") NativeMethods.NotifyError(ex.Message);
    return 1;
}

static async Task<int> Send(Request request)
{
    var reply = await Ipc.SendAsync(request, startAgent: true, CancellationToken.None);
    if (!reply.Success && request.Command is "launch" or "swap" or "download" or "settings") NativeMethods.NotifyError(reply.Message);
    Console.WriteLine(Json.Encode(reply)); return reply.Success ? 0 : 1;
}
sealed class NoInteraction : IUserInteraction
{
    public Task<bool> ConfirmAsync(string title, string text, CancellationToken ct) => Task.FromResult(false);
}
