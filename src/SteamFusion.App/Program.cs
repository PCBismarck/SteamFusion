using System.Windows;
using System.IO;
using SteamFusion.Core;
using SteamFusion.Windows;

namespace SteamFusion.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { NativeMethods.RequireOutsideSandbox(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "SteamFusion"); return 1; }
        using var mutex = new Mutex(true, @"Local\SteamFusion-" + Ipc.UserKey, out var first);
        if (!first)
        {
            if (!args.Contains("--agent"))
                try { Ipc.SendAsync(new("settings"), false, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SteamFusion;component/UiTheme.xaml", UriKind.Relative) });
        using var stopping = new CancellationTokenSource();
        var controller = new Controller(app);
        using var tray = new DesktopTray(app, controller);
        app.Startup += (_, _) =>
        {
            _ = Task.Run(() => Ipc.ServeAsync(controller.Handle, stopping.Token));
            if (!args.Contains("--agent")) controller.ShowSettings();
        };
        app.SessionEnding += (_, _) => tray.Dispose();
        app.Exit += (_, _) => { tray.Dispose(); stopping.Cancel(); };
        app.DispatcherUnhandledException += (_, e) =>
        { MessageBox.Show(e.Exception.Message, "SteamFusion"); e.Handled = true; };
        return app.Run();
    }
}

public sealed class Controller
{
    private readonly Application application;
    private readonly Router router;
    private readonly WindowsRuntime runtime;
    private SettingsWindow? settings;
    private CancellationTokenSource? operation;
    private readonly object operationLock = new();
    public event Action? SettingsClosed;
    public bool IsExiting { get; private set; }
    public Controller(Application application)
    {
        this.application = application;
        runtime = new(new DialogInteraction(application));
        router = new(runtime);
        router.Progress += phase =>
        {
            try { ConfigurationStore.AtomicWrite(Path.Combine(Discovery.DataDirectory, "status.json"), Json.Encode(new { time = DateTimeOffset.UtcNow, phase })); } catch (IOException) { }
            application.Dispatcher.BeginInvoke(() => settings?.SetStatus(phase));
        };
    }
    public void ShowSettings()
    {
        if (settings is null)
        {
            settings = new SettingsWindow(this);
            settings.Closed += (_, _) =>
            {
                settings = null;
                if (!IsExiting) SettingsClosed?.Invoke();
            };
        }
        settings.Show();
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }
    public bool Busy { get { lock (operationLock) return operation is not null; } }
    public Task<Reply> Handle(Request request)
    {
        switch (request.Command)
        {
            case "settings": application.Dispatcher.BeginInvoke(ShowSettings); return Task.FromResult(new Reply(true, "ok", "已打开设置。"));
            case "status": return Task.FromResult(new Reply(true, Busy ? "busy" : "idle", router.Phase, Json.Encode(router.LastOutcome)));
            case "cancel": lock (operationLock) operation?.Cancel(); return Task.FromResult(new Reply(true, "cancel_requested", "已请求取消；已完成的账号切换不会自动撤销。"));
            case "catalog-import":
                try
                {
                    List<LibrarySelection>? choices = null;
                    if (request.AppId != 0)
                    {
                        var row = CatalogStore.Preview(Discovery.Store.Load()).Games.SingleOrDefault(g => g.AppId == request.AppId && g.CanImport)
                            ?? throw new InvalidOperationException("该游戏没有可新增的账号路由。");
                        choices = [new(row.AppId, row.SuggestedAccountId!)];
                    }
                    var result = ImportLibrary(choices);
                    return Task.FromResult(new Reply(true, "imported", $"新增 {result.Added} 个游戏规则；原有规则保留。"));
                }
                catch (Exception ex) { return Task.FromResult(new Reply(false, "catalog", ex.Message)); }
            case "exit":
                lock (operationLock)
                {
                    if (operation is not null) return Task.FromResult(new Reply(false, "busy", "请先取消或等待当前操作结束，再退出后台。"));
                    _ = Task.Run(async () => { await Task.Delay(300); _ = application.Dispatcher.BeginInvoke(() =>
                    { IsExiting = true; application.Shutdown(); }); });
                    return Task.FromResult(new Reply(true, "exiting", "后台即将退出。"));
                }
            case "launch": case "swap": case "download": break;
            default: return Task.FromResult(new Reply(false, "unknown_command", "不支持的命令。"));
        }
        if (request.RequestId == Guid.Empty) return Task.FromResult(new Reply(false, "invalid_request", "请求 ID 缺失。"));
        lock (operationLock)
        {
            if (operation is not null) return Task.FromResult(new Reply(false, "busy", "已有操作在执行。"));
            Configuration config;
            try
            {
                config = Discovery.Store.Load();
                if (request.Command is "launch" or "download" && !config.Games.Any(g => g.AppId == request.AppId && g.Enabled))
                    throw new InvalidOperationException("请先在 SteamFusion 设置中确认并启用此游戏映射。");
                if (request.Command == "swap" && !config.Accounts.Any(a => a.Id == request.AccountId))
                    throw new InvalidOperationException("目标账号未配置。");
            }
            catch (Exception ex) { return Task.FromResult(new Reply(false, "configuration", ex.Message)); }
            operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var token = operation.Token;
            _ = Task.Run(async () =>
            {
                Outcome outcome;
                try
                {
                    outcome = request.Command switch
                    {
                        "launch" => await router.LaunchAsync(config, request.AppId, request.RequestId, token),
                        "download" => await router.OpenDownloadAsync(config, request.AppId, request.RequestId, token),
                        _ => await router.SwapAsync(config, request.AccountId!, request.RequestId, token)
                    };
                    ConfigurationStore.AtomicWrite(Path.Combine(Discovery.DataDirectory, "last-operation.json"),
                        Json.Encode(new { request.RequestId, request.Command, request.AppId, finishedAt = DateTimeOffset.UtcNow, outcome }));
                    if (!outcome.Success) _ = application.Dispatcher.BeginInvoke(() => MessageBox.Show(outcome.Message, "SteamFusion"));
                }
                catch (Exception ex) { _ = application.Dispatcher.BeginInvoke(() => MessageBox.Show(ex.Message, "SteamFusion")); }
                finally { lock (operationLock) { operation?.Dispose(); operation = null; } }
            });
            return Task.FromResult(new Reply(true, "accepted", "已接收请求，结果可在 SteamFusion 设置或 status 中查看。"));
        }
    }
    public Task<Snapshot> Probe(Configuration config) => runtime.ObserveAsync(config, CancellationToken.None);
    public LibraryImportResult ImportLibrary(IEnumerable<LibrarySelection>? selection = null)
    {
        lock (operationLock)
        {
            if (operation is not null) throw new InvalidOperationException("请等待当前启动或切换操作结束再导入。");
            return CatalogStore.Import(Discovery.Store.Load(), selection);
        }
    }
    public LibrarySyncResult SynchronizeLibrary(string fingerprint, IEnumerable<uint> selected)
    {
        lock (operationLock)
        {
            if (operation is not null) throw new InvalidOperationException("请等待当前启动或切换操作结束再同步。");
            return LibrarySyncStore.Current().Apply(fingerprint, selected);
        }
    }
}

public sealed class DialogInteraction(Application app) : IUserInteraction
{
    public async Task<bool> ConfirmAsync(string title, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await app.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ConfirmationWindow(title, text);
            using var registration = ct.Register(() => app.Dispatcher.BeginInvoke(() => dialog.Close()));
            return dialog.ShowDialog() == true;
        });
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
