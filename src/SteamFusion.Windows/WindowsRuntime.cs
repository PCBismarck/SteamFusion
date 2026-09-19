using Microsoft.Win32;
using SteamFusion.Core;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace SteamFusion.Windows;

public interface IUserInteraction
{
    Task<bool> ConfirmAsync(string title, string text, CancellationToken ct);
}

public sealed record ClientSignal(int Pid, string SteamId, bool LoggedOn, uint[] RunningGames, bool ActivityKnown,
    long Timestamp, string Environment);

public sealed class WindowsRuntime(IUserInteraction interaction) : IRuntime
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> confirmations = new();
    public Task<bool> ConfirmSaveEnvironmentAsync(Game game, Route route, CancellationToken ct) =>
        interaction.ConfirmAsync("确认游戏存档", $"{game.Name} 的存档方式尚未配置。\n\n即将使用：{route.Account.Name} / {route.Environment}\n普通环境和沙盒可能使用不同的本地存档。请确认该环境存档正确；需要迁移时先取消，在设置中固定运行环境或配置 Steam 云存档。\n\n是否继续？", ct);
    public Task<Snapshot> ObserveAsync(Configuration config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<Instance>(); bool unmanaged = false;
        using var sandbox = new SandboxApi(config.SandboxieStartExe);
        var sandboxService = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SbieSvc", "ImagePath", null);
        if (sandboxService is not null && !sandbox.Available)
            throw new InvalidOperationException("检测到 Sandboxie，但未能载入其进程查询接口。请设置正确的 Start.exe 路径。");
        using var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        var activePid = Convert.ToInt32(active?.GetValue("pid") ?? 0);
        var user32 = unchecked((uint)Convert.ToInt32(active?.GetValue("ActiveUser") ?? 0));
        var nativeId = user32 == 0 ? null : (76561197960265728UL + user32).ToString();
        var running = RunningGames();
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("steam"))
        using (process)
        {
            try
            {
                if (process.SessionId != self.SessionId) { unmanaged = true; continue; }
                var environment = sandbox.GetBox(process.Id) ?? "native";
                if (environment != "native" && !config.Accounts.Any(a => a.SandboxName == environment))
                { unmanaged = true; continue; }
                var ticks = process.StartTime.ToUniversalTime().Ticks;
                var signal = ReadSignal(environment, process.Id, ticks);
                var candidate = environment == "native" && process.Id == activePid ? nativeId : null;
                if (signal is not null) candidate = signal.LoggedOn ? signal.SteamId : null;
                var key = $"{environment}:{process.Id}:{ticks}:{candidate}";
                var verified = candidate is not null && (signal?.LoggedOn == true ||
                    confirmations.TryGetValue(key, out var expires) && expires > DateTimeOffset.UtcNow);
                // Without an identity signal a sandbox is explicitly unknown, never inferred from its name.
                if (candidate is null && signal is null)
                {
                    var prefix = $"{environment}:{process.Id}:{ticks}:";
                    var attested = confirmations.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value > DateTimeOffset.UtcNow);
                    if (attested.Key is not null) { candidate = attested.Key[prefix.Length..]; verified = true; }
                }
                result.Add(new(environment, process.Id, ticks, candidate, verified,
                    (signal?.RunningGames ?? []).Concat(environment == "native" ? running.Ids : []).Distinct().ToArray(),
                    signal?.ActivityKnown == true || environment == "native" && running.Known,
                    signal?.LoggedOn == true ? "Steam 插件实时身份" : verified ? "用户确认的当前会话" : "需要确认登录账号"));
            }
            catch (InvalidOperationException) { if (!HasExited(process)) unmanaged = true; }
            catch (System.ComponentModel.Win32Exception) { if (!HasExited(process)) unmanaged = true; }
        }
        if (result.Count(i => i.IsNative) > 1 || result.GroupBy(i => i.Environment).Any(g => g.Count() > 1))
            return Task.FromResult(new Snapshot([], true));
        return Task.FromResult(new Snapshot(result, unmanaged));
    }

    public async Task<Instance> EnsureReadyAsync(Configuration config, Account account, string environment, CancellationToken ct)
    {
        var snapshot = await ObserveAsync(config, ct);
        if (snapshot.HasUnmanagedInstances) throw new InvalidOperationException("发现未受管理的实例。");
        var instance = snapshot.Instances.SingleOrDefault(i => i.Environment == environment);
        if (instance is null)
        {
            if (environment == "native") Start(config.SteamExe, "-silent");
            else { RequireSandbox(config); Start(config.SandboxieStartExe, "/box:" + environment, config.SteamExe, "-silent"); }
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (instance is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, ct);
                var observation = await ObserveAsync(config, ct);
                if (observation.HasUnmanagedInstances) throw new InvalidOperationException("启动后实例归属不明。");
                instance = observation.Instances.SingleOrDefault(i => i.Environment == environment);
            }
            if (instance is null) throw new InvalidOperationException($"{environment} 未成功启动，请检查 Steam 与沙盒配置。");
        }
        // Give the optional client plugin time to report actual login identity.
        for (int n = 0; n < 8 && !instance.Verified; n++)
        {
            await Task.Delay(500, ct);
            instance = (await ObserveAsync(config, ct)).Instances.SingleOrDefault(i => i.Environment == environment)
                ?? throw new InvalidOperationException("Steam 在等待登录时退出。");
        }
        if (instance.Verified && instance.SteamId == account.SteamId) return instance;
        if (instance.SteamId is not null && instance.SteamId != account.SteamId)
            throw new InvalidOperationException($"{environment} 当前不是 {account.Name}，请在对应 Steam 中登录正确账号。");
        var before = instance;
        if (!await interaction.ConfirmAsync("确认 Steam 登录", $"请在 {environment} 的 Steam 窗口中完成登录。\n\n目标：{account.Name}\n登录用户名：{account.LoginName}\nSteamID：{account.SteamId}\n\n确认窗口显示的是这个账号后，点击“是”。此确认只用于当前进程会话。", ct))
            throw new OperationCanceledException();
        var after = (await ObserveAsync(config, ct)).Instances.SingleOrDefault(i => i.Environment == environment);
        if (after is null || before.Pid != after.Pid || before.StartTicks != after.StartTicks ||
            after.SteamId is not null && after.SteamId != account.SteamId)
            throw new InvalidOperationException("登录确认期间实例发生变化，请重试。");
        confirmations[$"{environment}:{after.Pid}:{after.StartTicks}:{account.SteamId}"] = DateTimeOffset.UtcNow.AddMinutes(2);
        return after with { SteamId = account.SteamId, Verified = true };
    }

    public async Task<bool> ConfirmSwitchAsync(Configuration config, Snapshot snapshot, Account target, CancellationToken ct)
    {
        if (snapshot.Instances.Count == 0) return true;
        // There is no stable public cloud-completion API. Never turn Unknown into 'synced'.
        return await interaction.ConfirmAsync("切换前确认", $"准备将 {target.Name} 用作普通客户端账号。\n\n请确认两个 Steam 中的游戏均已退出、下载已暂停、云存档显示同步完成。\n\nSteamFusion 不会强制结束游戏，也不会自动处理云存档冲突。确认后将正常退出相关客户端。", ct);
    }

    public async Task StopAsync(Configuration config, Instance instance, CancellationToken ct)
    {
        var fresh = await ObserveAsync(config, ct);
        if (fresh.HasUnmanagedInstances) throw new InvalidOperationException("退出前检测到无法确认归属的 Steam 进程，请稍后重试。");
        if (!fresh.Instances.Any(i => i.Identity == instance.Identity))
            throw new InvalidOperationException($"退出前 {instance.Environment} 的进程或登录账号发生变化，请重试。");
        if (fresh.Instances.Any(i => i.RunningGames.Length > 0))
            throw new InvalidOperationException("退出前检测到正在运行的游戏，请先退出游戏。");
        // Hold the original process handle. A temporarily ambiguous snapshot must
        // never be interpreted as proof that Steam finished shutting down.
        using var original = Process.GetProcessById(instance.Pid);
        if (original.StartTime.ToUniversalTime().Ticks != instance.StartTicks)
            throw new InvalidOperationException("退出前 Steam 进程已被替换，请重试。");
        if (instance.IsNative) Start(config.SteamExe, "-shutdown");
        else Start(config.SandboxieStartExe, "/box:" + instance.Environment, config.SteamExe, "-shutdown");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(400, ct);
            if (original.HasExited) return;
        }
        throw new InvalidOperationException("Steam 未正常退出。请处理 Steam 的提示，程序没有强制终止进程。");
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public async Task SelectNativeAccountAsync(Configuration config, Account account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NativeMethods.RequireOutsideSandbox();
        void RequireStopped()
        {
            var processes = Process.GetProcessesByName("steam");
            try { if (processes.Length > 0) throw new InvalidOperationException("Steam 尚未完全退出，未修改登录账号。"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true)
            ?? throw new InvalidOperationException("找不到 Steam 登录设置，请先在普通 Steam 登录一次。");
        NativeAccountSelection.Select(config.SteamExe, account, key, RequireStopped);
        RequireStopped();
        using var started = Start(config.SteamExe);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var observed = await ObserveAsync(config, ct);
            if (observed.HasUnmanagedInstances) throw new InvalidOperationException("切号期间出现未知 Steam 实例。");
            if (observed.Native is not null) return;
        }
        throw new InvalidOperationException("Steam 尚未启动，请检查客户端提示。登录失效时需在 Steam 中重新验证。");
    }

    public Task LaunchAsync(Configuration config, Instance instance, uint appId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (instance.IsNative) Start(config.SteamExe, "-applaunch", appId.ToString());
        else Start(config.SandboxieStartExe, "/box:" + instance.Environment, config.SteamExe, "-applaunch", appId.ToString());
        return Task.CompletedTask;
    }

    public Task OpenLibraryAsync(Configuration config, Instance instance, uint appId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uri = "steam://nav/games/details/" + appId;
        if (instance.IsNative) Start(config.SteamExe, uri);
        else if (config.SharedLibraryDownloads) Start(config.SandboxieStartExe, "/box:" + instance.Environment, config.SteamExe, uri);
        else throw new InvalidOperationException("请先启用共享游戏库下载，或使用普通 Steam 下载。");
        return Task.CompletedTask;
    }

    public static Process Start(string exe, params string[] arguments)
    {
        if (!Path.IsPathFullyQualified(exe) || !File.Exists(exe)) throw new FileNotFoundException("找不到程序，请检查设置。", exe);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("进程启动失败。");
    }

    private static void RequireSandbox(Configuration config)
    {
        if (!File.Exists(config.SandboxieStartExe)) throw new InvalidOperationException("沙盒路由需要 Sandboxie-Plus，请先安装并在设置中选择 Start.exe。");
    }
    private static (uint[] Ids, bool Known) RunningGames()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps");
        if (root is null) return ([], false);
        var ids = new List<uint>();
        foreach (var key in root.GetSubKeyNames())
        {
            using var app = root.OpenSubKey(key);
            if (uint.TryParse(key, out var id) && Convert.ToInt32(app?.GetValue("Running") ?? 0) == 1) ids.Add(id);
        }
        return (ids.ToArray(), true);
    }
    private static ClientSignal? ReadSignal(string environment, int pid, long ticks)
    {
        try
        {
            var path = Path.Combine(Discovery.DataDirectory, "signals", environment + ".json");
            if (!File.Exists(path)) return null;
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified.Ticks < ticks || (DateTime.UtcNow - modified).TotalSeconds > 8) return null;
            var signal = Json.Decode<ClientSignal>(File.ReadAllText(path));
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - signal.Timestamp;
            return signal.Pid == pid && signal.Environment == environment && age is >= -2000 and < 8000 ? signal : null;
        }
        catch (IOException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
