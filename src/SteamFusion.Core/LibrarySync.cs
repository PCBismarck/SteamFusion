using System.Security.Cryptography;
using System.Text;

namespace SteamFusion.Core;

public enum LibrarySyncAction { Add, Reassign, Disable, Review, Keep }
public sealed record LibrarySyncChange(uint AppId, string Name, LibrarySyncAction Action, Game? Before, Game? After, string Reason)
{
    public bool CanApply => Action is LibrarySyncAction.Add or LibrarySyncAction.Reassign or LibrarySyncAction.Disable;
}
public sealed record LibrarySyncAccount(string AccountId, DateTimeOffset? CapturedAt, bool Ready, string Message);
public sealed record LibrarySyncPreview(string Fingerprint, bool Ready, List<LibrarySyncAccount> Accounts, List<LibrarySyncChange> Changes);

public static class LibrarySync
{
    public static LibrarySyncPreview Preview(Configuration config, IEnumerable<LibrarySnapshot> snapshots, DateTimeOffset since, DateTimeOffset now, bool preferNative = false)
    {
        config.Validate();
        if (since == default || since > now) throw new InvalidDataException("同步开始时间无效。");
        var valid = new Dictionary<string, Dictionary<uint, LibraryGame>>();
        var states = new List<LibrarySyncAccount>();
        var input = snapshots.ToList();
        foreach (var account in config.Accounts)
        {
            var snapshot = input.Where(s => s.SteamId == account.SteamId).OrderByDescending(s => s.CapturedAt).FirstOrDefault();
            var message = "等待在普通 Steam 登录并加载此账号游戏库";
            if (snapshot is not null)
            {
                try
                {
                    snapshot.Validate();
                    if (!snapshot.IncludesHiddenGames) message = "旧记录不含隐藏游戏，请在普通 Steam 重新采集";
                    else if (snapshot.CapturedAt < since || snapshot.CapturedAt < now.AddHours(-24) || snapshot.CapturedAt > now.AddMinutes(5))
                        message = "等待本轮的新记录（有效期 24 小时）";
                    else if (snapshot.Games.Count == 0) message = "游戏库为空，暂不据此停用已有规则，请核对加载状态";
                    else { valid.Add(account.Id, snapshot.Games.ToDictionary(g => g.AppId)); message = "已采集本轮记录"; }
                }
                catch (InvalidDataException) { message = "记录无效，请重新采集"; }
            }
            states.Add(new(account.Id, snapshot?.CapturedAt, valid.ContainsKey(account.Id), message));
        }
        // Ignore capture time in the review fingerprint: periodic captures of the same
        // content must not invalidate a user's selection, but changed licenses must.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Encode(new
        {
            config, since, preferNative,
            evidence = valid.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { account = p.Key, games = p.Value.Values.OrderBy(g => g.AppId) })
        }))));
        if (config.Accounts.Count == 0 || valid.Count != config.Accounts.Count) return new(fingerprint, false, states, []);
        var existing = config.Games.ToDictionary(g => g.AppId);
        var ids = valid.Values.SelectMany(d => d.Keys).Concat(existing.Keys).Distinct().Order().ToList();
        var changes = new List<LibrarySyncChange>();
        foreach (var id in ids)
        {
            existing.TryGetValue(id, out var before);
            var records = config.Accounts.Select(a => (Account: a, Game: valid[a.Id].GetValueOrDefault(id))).ToList();
            var access = records.Where(r => r.Game?.Subscribed == true).ToList();
            var target = access.OrderByDescending(r => r.Account.Id == config.DefaultNativeAccountId)
                .ThenBy(r => r.Game!.OwnerAccountId != 0).ThenBy(r => r.Account.Id, StringComparer.Ordinal).FirstOrDefault();
            var name = before?.Name ?? records.First(r => r.Game is not null).Game!.Name;
            void Add(LibrarySyncAction action, Game? after, string reason) => changes.Add(new(id, name, action, before, after, reason));
            if (before is null)
            {
                if (target.Account is not null)
                    Add(LibrarySyncAction.Add, new(id, name, target.Account.Id) { Enabled = true,
                        Saves = target.Game!.CloudEnabled == true ? SaveMode.SteamCloud : SaveMode.Unknown }, "新增可用游戏；家庭共享仅作为可用账号记录");
                else Add(LibrarySyncAction.Review, null, "没有已确认的可用许可，不自动新增");
                continue;
            }
            if (!before.Enabled) { Add(LibrarySyncAction.Keep, before, "保留已停用规则；如需恢复请在设置中启用"); continue; }
            if (preferNative && before.AccountId != config.DefaultNativeAccountId && target.Account?.Id == config.DefaultNativeAccountId)
            {
                if (before.Saves == SaveMode.FixedEnvironment)
                    Add(LibrarySyncAction.Review, before, "普通账号可用，但存档固定了运行环境；请先处理存档后手动改账号");
                else Add(LibrarySyncAction.Reassign, before with { AccountId = target.Account.Id, Saves = SaveMode.Unknown },
                    "按优先普通账号重新分配；保留运行设置，存档改为待核对，不迁移存档");
                continue;
            }
            var current = valid[before.AccountId].GetValueOrDefault(id);
            if (current?.Subscribed == true) { Add(LibrarySyncAction.Keep, before, "原账号仍可用，保留手动选择和运行设置"); continue; }
            if (current is not null && current.Subscribed is null)
            { Add(LibrarySyncAction.Review, before, "原账号的许可状态未知，保留规则并等待核对"); continue; }
            if (target.Account is not null)
            {
                if (before.Saves == SaveMode.FixedEnvironment)
                    Add(LibrarySyncAction.Review, before, "可用账号已变化，但存档固定了运行环境；请先核对存档，再手动修改账号");
                else Add(LibrarySyncAction.Reassign, before with { AccountId = target.Account.Id, Saves = SaveMode.Unknown },
                    "原账号已无可用记录；建议改账号。运行方式保留，存档改为待核对，首次启动会提示");
            }
            else if (records.Any(r => r.Game is not null && r.Game.Subscribed is null))
                Add(LibrarySyncAction.Review, before, "有账号许可状态未知，不据此停用");
            else Add(LibrarySyncAction.Disable, before with { Enabled = false }, "两个账号的新记录均无可用许可；建议停用入口，保留规则与游戏文件");
        }
        return new(fingerprint, true, states, changes.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    public static Configuration Apply(Configuration config, LibrarySyncPreview preview, string reviewedFingerprint, IEnumerable<uint> selected)
    {
        if (!preview.Ready || preview.Fingerprint != reviewedFingerprint) throw new InvalidOperationException("游戏库或配置已变化，请刷新预览后重新选择。");
        var ids = selected.ToList();
        if (ids.Count == 0 || ids.Count != ids.Distinct().Count()) throw new InvalidDataException("请选择不重复的变更项。");
        var games = config.Games.ToDictionary(g => g.AppId);
        foreach (var id in ids)
        {
            var change = preview.Changes.SingleOrDefault(c => c.AppId == id);
            if (change is null || !change.CanApply || change.After is null || games.GetValueOrDefault(id) != change.Before)
                throw new InvalidOperationException("所选规则已变化或需要手动核对，请刷新预览。");
            games[id] = change.After;
        }
        var updated = config with { Games = games.Values.ToList() }; updated.Validate(); return updated;
    }
}
