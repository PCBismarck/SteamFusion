namespace SteamFusion.Core;

/// <summary>Serializes mutations. It never repeats an interrupted launch after process restart.</summary>
public sealed class Router(IRuntime runtime)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, (string Fingerprint, Outcome Result)> completed = [];
    public string Phase { get; private set; } = "就绪";
    public Outcome? LastOutcome { get; private set; }
    public bool Busy => gate.CurrentCount == 0;
    public event Action<string>? Progress;

    public Task<Outcome> LaunchAsync(Configuration config, uint appId, Guid requestId, CancellationToken ct = default)
        => RunAsync(requestId, $"launch:{appId}", async () =>
        {
            var game = config.Games.SingleOrDefault(g => g.AppId == appId)
                ?? throw new InvalidOperationException("该游戏没有配置路由，请先在设置中添加。");
            SetPhase("检查账号与运行环境");
            var snapshot = await runtime.ObserveAsync(config, ct);
            var route = RoutePlanner.Plan(config, game, snapshot);
            string? warning = null;
            if (route.RequiresSwap && !game.AutoSwap)
                return Outcome.Fail("manual_swap", "此游戏设置为手动切号，请先交换账号位置。");
            if (game.Saves == SaveMode.Unknown && (route.Environment != "native" || route.RequiresSwap) &&
                !await runtime.ConfirmSaveEnvironmentAsync(game, route, ct))
                throw new OperationCanceledException();
            if (route.RequiresSwap)
            {
                warning = await SwitchCore(config, route.Account, snapshot, game.PauseOtherSandbox, ct);
            }
            else if (game.PauseOtherSandbox)
                await PauseSandboxes(config, snapshot, route.Account, ct);
            SetPhase($"等待 {route.Account.Name} 登录");
            var instance = await runtime.EnsureReadyAsync(config, route.Account, route.Environment, ct);
            if (!instance.Verified || instance.SteamId != route.Account.SteamId)
                return Outcome.Fail("identity_mismatch", "登录账号未确认，未启动游戏。");
            // A second live observation prevents an intervening manual account change from using old state.
            var current = await runtime.ObserveAsync(config, ct);
            if (current.HasUnmanagedInstances || !current.Instances.Any(i => i.Identity == instance.Identity && i.Verified))
                return Outcome.Fail("state_changed", "Steam 状态在启动前发生变化，请重新尝试。");
            SetPhase($"启动 {game.Name}");
            await runtime.LaunchAsync(config, instance, appId, ct);
            return Outcome.Ok($"已向 {route.Account.Name} 的 {route.Environment} 实例提交 {game.Name} 启动请求。" + warning, "submitted");
        }, ct);

    public Task<Outcome> OpenDownloadAsync(Configuration config, uint appId, Guid requestId, CancellationToken ct = default)
        => RunAsync(requestId, $"download:{appId}", async () =>
        {
            config.Validate();
            var game = config.Games.SingleOrDefault(g => g.AppId == appId && g.Enabled)
                ?? throw new InvalidOperationException("请先确认此游戏的游玩账号并启用映射。");
            var target = config.Accounts.Single(a => a.Id == game.AccountId);
            SetPhase($"准备到 {target.Name} 下载 {game.Name}");
            var snapshot = await runtime.ObserveAsync(config, ct);
            if (snapshot.HasUnmanagedInstances) throw new InvalidOperationException("有未受管理的 Steam 实例，暂不打开下载页面。");
            var route = config.SharedLibraryDownloads
                ? RoutePlanner.Plan(config, game with { Mode = RunMode.Auto, Saves = SaveMode.Unknown, FixedEnvironment = null }, snapshot)
                : new Route(target, "native", snapshot.Native is not { Verified: true } native || native.SteamId != target.SteamId);
            string? warning = null;
            if (route.RequiresSwap)
                warning = await SwitchCore(config, target, snapshot, false, ct);
            var instance = await runtime.EnsureReadyAsync(config, target, route.Environment, ct);
            if (instance.Environment != route.Environment || !instance.Verified || instance.SteamId != target.SteamId)
                return Outcome.Fail("identity_mismatch", "客户端登录账号未确认，未打开下载页面。");
            var current = await runtime.ObserveAsync(config, ct);
            if (current.HasUnmanagedInstances || !current.Instances.Any(i => i.Identity == instance.Identity && i.Verified))
                return Outcome.Fail("state_changed", "Steam 状态已变化，请重新打开下载页面。");
            await runtime.OpenLibraryAsync(config, instance, appId, ct);
            return Outcome.Ok($"已在 {target.Name} 的 Steam 打开 {game.Name}，请在 Steam 中选择安装到共享游戏库。" + warning, "download_ready");
        }, ct);

    public Task<Outcome> SwapAsync(Configuration config, string accountId, Guid requestId, CancellationToken ct = default)
        => RunAsync(requestId, $"swap:{accountId}", async () =>
        {
            config.Validate();
            var target = config.Accounts.SingleOrDefault(a => a.Id == accountId)
                ?? throw new InvalidOperationException("目标账号不存在。");
            var snapshot = await runtime.ObserveAsync(config, ct);
            if (snapshot.Native is { Verified: true } native && native.SteamId == target.SteamId)
                return Outcome.Ok("该账号已经在普通客户端。");
            var warning = await SwitchCore(config, target, snapshot, false, ct);
            return Outcome.Ok($"普通客户端现在使用 {target.Name}。" + warning);
        }, ct);

    private async Task<string?> SwitchCore(Configuration config, Account target, Snapshot snapshot, bool pauseOther, CancellationToken ct)
    {
        if (snapshot.HasUnmanagedInstances) throw new InvalidOperationException("有未受管理的 Steam 实例，无法交换。");
        if (snapshot.Instances.Any(i => i.RunningGames.Length > 0))
            throw new InvalidOperationException("请先退出正在运行的游戏，再切换账号。");
        SetPhase("等待云存档与退出确认");
        if (!await runtime.ConfirmSwitchAsync(config, snapshot, target, ct))
            throw new OperationCanceledException("已取消账号切换。");
        // Re-read after the user dialog: a game could have started while it was open.
        var fresh = await runtime.ObserveAsync(config, ct);
        if (fresh.HasUnmanagedInstances || fresh.Instances.Any(i => i.RunningGames.Length > 0) ||
            !SameProcesses(snapshot, fresh)) throw new InvalidOperationException("确认期间 Steam 状态发生变化，已停止切换。");
        foreach (var instance in fresh.Instances.OrderBy(i => i.IsNative))
        {
            SetPhase($"正常退出 {instance.Environment}");
            await runtime.StopAsync(config, instance, ct);
        }
        var stopped = await runtime.ObserveAsync(config, ct);
        if (stopped.HasUnmanagedInstances || stopped.Instances.Count != 0)
            throw new InvalidOperationException("Steam 尚未完全退出，未执行切号。");
        SetPhase($"切换普通 Steam 到 {target.Name}");
        await runtime.SelectNativeAccountAsync(config, target, ct);
        var selected = await runtime.EnsureReadyAsync(config, target, "native", ct);
        if (!selected.Verified || selected.SteamId != target.SteamId)
            throw new InvalidOperationException("目标账号尚未登录成功。");
        if (config.KeepBothOnline && !pauseOther)
        {
            var other = config.Accounts.SingleOrDefault(a => a.Id != target.Id);
            if (other is not null)
            {
                try { await runtime.EnsureReadyAsync(config, other, other.SandboxName, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { return $"普通账号已就绪；另一个沙盒未启动：{ex.Message}"; }
            }
        }
        return null;
    }

    private async Task PauseSandboxes(Configuration config, Snapshot snapshot, Account target, CancellationToken ct)
    {
        if (!snapshot.Instances.Any(i => !i.IsNative)) return;
        if (snapshot.Instances.Any(i => !i.IsNative && i.RunningGames.Length > 0))
            throw new InvalidOperationException("请先退出沙盒中的游戏。");
        if (!await runtime.ConfirmSwitchAsync(config, snapshot, target, ct)) throw new OperationCanceledException();
        var fresh = await runtime.ObserveAsync(config, ct);
        if (fresh.HasUnmanagedInstances || !SameProcesses(snapshot, fresh) || fresh.Instances.Any(i => i.RunningGames.Length > 0))
            throw new InvalidOperationException("确认期间实例状态变化。");
        foreach (var instance in fresh.Instances.Where(i => !i.IsNative)) await runtime.StopAsync(config, instance, ct);
    }

    private static bool SameProcesses(Snapshot a, Snapshot b) =>
        a.Instances.Select(i => i.Identity).Order().SequenceEqual(b.Instances.Select(i => i.Identity).Order());

    private async Task<Outcome> RunAsync(Guid id, string fingerprint, Func<Task<Outcome>> action, CancellationToken ct)
    {
        if (id == Guid.Empty) return Outcome.Fail("invalid_request", "请求 ID 无效。");
        if (!await gate.WaitAsync(0, ct)) return Outcome.Fail("busy", "已有启动或切换操作正在执行。");
        try
        {
            if (completed.TryGetValue(id, out var prior)) return prior.Fingerprint == fingerprint ? prior.Result :
                Outcome.Fail("request_conflict", "请求 ID 已被另一操作使用。");
            Outcome result;
            try { result = await action(); }
            catch (OperationCanceledException) { result = Outcome.Fail("cancelled", "操作已取消或超时，未自动重试。"); }
            catch (Exception ex) { result = Outcome.Fail("failed", ex.Message); }
            completed[id] = (fingerprint, result);
            if (completed.Count > 256) completed.Remove(completed.Keys.First());
            LastOutcome = result;
            SetPhase(result.Message);
            return result;
        }
        finally { gate.Release(); }
    }
    private void SetPhase(string phase) { Phase = phase; Progress?.Invoke(phase); }
}
