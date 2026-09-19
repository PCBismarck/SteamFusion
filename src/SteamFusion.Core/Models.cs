using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamFusion.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException("JSON 内容为空。");
}

public enum RunMode { Auto, NativeOnly }
public enum SaveMode { Unknown, SteamCloud, FixedEnvironment }
public sealed record Account(string Id, string SteamId, string LoginName, string Name, string SandboxName);
public sealed record Game(uint AppId, string Name, string AccountId)
{
    public bool Enabled { get; init; }
    public RunMode Mode { get; init; } = RunMode.Auto;
    public bool AutoSwap { get; init; } = true;
    public SaveMode Saves { get; init; } = SaveMode.Unknown;
    public string? FixedEnvironment { get; init; }
    public bool PauseOtherSandbox { get; init; }
}
public sealed record Configuration
{
    public int SchemaVersion { get; init; } = 1;
    public string SteamExe { get; init; } = "";
    public string WattExe { get; init; } = "";
    public string SandboxieStartExe { get; init; } = "";
    public string DefaultNativeAccountId { get; init; } = "";
    public bool KeepBothOnline { get; init; } = true;
    public bool IntegrateLibrary { get; init; }
    public bool UninstalledLibrary { get; init; }
    public bool SharedLibraryDownloads { get; init; }
    public List<Account> Accounts { get; init; } = [];
    public List<Game> Games { get; init; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的配置版本。");
        if (Accounts.Count > 2) throw new InvalidDataException("首版最多配置两个账号。");
        Unique(Accounts.Select(a => a.Id), "账号 ID");
        Unique(Accounts.Select(a => a.SteamId), "SteamID");
        Unique(Accounts.Select(a => a.LoginName), "登录用户名");
        Unique(Accounts.Select(a => a.SandboxName), "沙盒名");
        Unique(Games.Select(g => g.AppId.ToString()), "游戏 AppID");
        foreach (var account in Accounts)
        {
            if (!Regex.IsMatch(account.Id, "^[A-Za-z0-9_-]{1,40}$") ||
                !Regex.IsMatch(account.SteamId, "^765[0-9]{14}$") ||
                !Regex.IsMatch(account.LoginName, "^[A-Za-z0-9_]{1,64}$") ||
                !Regex.IsMatch(account.SandboxName, "^[A-Za-z][A-Za-z0-9_]{0,31}$"))
                throw new InvalidDataException("账号 ID、SteamID、登录用户名或沙盒名格式有误。");
        }
        if (DefaultNativeAccountId.Length > 0 && !Accounts.Any(a => a.Id == DefaultNativeAccountId))
            throw new InvalidDataException("默认普通账号不存在。");
        foreach (var game in Games)
        {
            if (game.AppId == 0 || !Accounts.Any(a => a.Id == game.AccountId))
                throw new InvalidDataException($"游戏 {game.Name} 没有有效的账号映射。");
            if (!Enum.IsDefined(game.Mode) || !Enum.IsDefined(game.Saves))
                throw new InvalidDataException("游戏策略无效。");
            if (game.Saves == SaveMode.FixedEnvironment && game.FixedEnvironment is not "native" &&
                game.FixedEnvironment != Accounts.Single(a => a.Id == game.AccountId).SandboxName)
                throw new InvalidDataException("固定存档环境必须是 native 或该账号的沙盒名。");
        }
    }
    private static void Unique(IEnumerable<string> items, string name)
    {
        var values = items.ToArray();
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new InvalidDataException($"{name}重复。");
    }
}

public sealed record Instance(string Environment, int Pid, long StartTicks, string? SteamId,
    bool Verified, uint[] RunningGames, bool ActivityKnown, string Detail)
{
    public bool IsNative => Environment == "native";
    public string Identity => $"{Environment}:{Pid}:{StartTicks}:{SteamId}";
}
public sealed record Snapshot(List<Instance> Instances, bool HasUnmanagedInstances = false)
{
    public Instance? Native => Instances.SingleOrDefault(i => i.IsNative);
}
public sealed record Route(Account Account, string Environment, bool RequiresSwap);
public sealed record Outcome(bool Success, string Code, string Message)
{
    public static Outcome Ok(string message, string code = "ok") => new(true, code, message);
    public static Outcome Fail(string code, string message) => new(false, code, message);
}

public static class RoutePlanner
{
    public static Route Plan(Configuration config, Game game, Snapshot snapshot)
    {
        config.Validate();
        if (!game.Enabled) throw new InvalidOperationException("游戏映射尚未启用，请先在设置中确认游玩账号。");
        if (snapshot.HasUnmanagedInstances) throw new InvalidOperationException("发现未受管理的 Steam 实例，暂不路由。");
        var account = config.Accounts.Single(a => a.Id == game.AccountId);
        var nativeId = snapshot.Native?.SteamId;
        var nativeAccount = nativeId is null ? config.DefaultNativeAccountId :
            config.Accounts.SingleOrDefault(a => a.SteamId == nativeId)?.Id;
        if (snapshot.Native is not null && nativeAccount is null)
            throw new InvalidOperationException("普通 Steam 当前账号不明，请先确认登录身份。");
        var environment = game.Mode == RunMode.NativeOnly || game.Saves == SaveMode.FixedEnvironment && game.FixedEnvironment == "native" ||
            nativeAccount == account.Id ? "native" : account.SandboxName;
        if (game.Saves == SaveMode.FixedEnvironment && game.FixedEnvironment != environment)
            throw new InvalidOperationException("此游戏固定了存档运行环境；请先处理存档迁移，再修改规则。");
        // A configured default is a desired layout, not evidence of Steam's next auto-login.
        return new(account, environment, environment == "native" && (snapshot.Native is null || nativeAccount != account.Id));
    }
}

public interface IRuntime
{
    Task<Snapshot> ObserveAsync(Configuration config, CancellationToken ct);
    Task<bool> ConfirmSaveEnvironmentAsync(Game game, Route route, CancellationToken ct);
    Task<Instance> EnsureReadyAsync(Configuration config, Account account, string environment, CancellationToken ct);
    Task<bool> ConfirmSwitchAsync(Configuration config, Snapshot snapshot, Account target, CancellationToken ct);
    Task StopAsync(Configuration config, Instance instance, CancellationToken ct);
    Task SelectNativeAccountAsync(Configuration config, Account account, CancellationToken ct);
    Task LaunchAsync(Configuration config, Instance instance, uint appId, CancellationToken ct);
    Task OpenLibraryAsync(Configuration config, Instance instance, uint appId, CancellationToken ct);
}
