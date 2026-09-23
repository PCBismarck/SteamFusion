namespace SteamFusion.Core;

public sealed record LaunchAccount(string AccountId, string SteamId, string AccountName, DateTimeOffset ExpiresAt);
public sealed record AccountLaunchPlan(Configuration Configuration, bool ConfirmAccountChange);

public static class AccountLaunch
{
    public static Dictionary<uint, List<LaunchAccount>> Available(Configuration config, IEnumerable<LibrarySnapshot> snapshots, DateTimeOffset now)
    {
        var result = new Dictionary<uint, List<LaunchAccount>>();
        foreach (var account in config.Accounts)
        {
            var snapshot = snapshots.Where(s => s.SteamId == account.SteamId).OrderByDescending(s => s.CapturedAt).FirstOrDefault();
            if (snapshot is null) continue;
            snapshot.Validate();
            if (snapshot.CapturedAt < now.AddDays(-7) || snapshot.CapturedAt > now.AddMinutes(5)) continue;
            foreach (var game in snapshot.Games.Where(g => g.Subscribed == true))
            {
                if (!result.TryGetValue(game.AppId, out var accounts)) result[game.AppId] = accounts = [];
                accounts.Add(new(account.Id, account.SteamId, account.Name, snapshot.CapturedAt.AddDays(7)));
            }
        }
        return result;
    }
    public static AccountLaunchPlan Prepare(Configuration config, uint appId, string steamId, IEnumerable<LibrarySnapshot> snapshots, DateTimeOffset now)
    {
        config.Validate();
        var game = config.Games.SingleOrDefault(g => g.AppId == appId && g.Enabled)
            ?? throw new InvalidOperationException("该游戏没有启用路由。");
        var available = Available(config, snapshots, now).GetValueOrDefault(appId);
        if (available?.Count != 2) throw new InvalidOperationException("两个账号的可用许可尚未确认或已过期，请刷新游戏库后重试。");
        var target = available.SingleOrDefault(a => a.SteamId == steamId)
            ?? throw new InvalidOperationException("所选账号没有已确认的可用许可。");
        var changed = game.AccountId != target.AccountId;
        if (changed && game.Saves == SaveMode.FixedEnvironment)
            throw new InvalidOperationException("此游戏固定了存档环境，请先处理存档并修改设置，再选择另一个账号。");
        var selected = changed ? game with { AccountId = target.AccountId, Saves = SaveMode.Unknown } : game;
        var transient = config with { Games = config.Games.Select(g => g.AppId == appId ? selected : g).ToList() };
        transient.Validate(); return new(transient, changed);
    }
}
