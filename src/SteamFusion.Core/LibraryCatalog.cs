namespace SteamFusion.Core;

public sealed record LibraryGame(uint AppId, string Name, bool? Subscribed, uint OwnerAccountId, bool Installed, bool? CloudEnabled);
public sealed record LibrarySnapshot(int SchemaVersion, string Source, string SteamId, DateTimeOffset CapturedAt, List<LibraryGame> Games)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || Source != "steam-client" || !System.Text.RegularExpressions.Regex.IsMatch(SteamId ?? "", "^765[0-9]{14}$") ||
            Games is null || Games.Count > 30000 || CapturedAt == default)
            throw new InvalidDataException("游戏库快照格式无效。");
        var ids = new HashSet<uint>();
        foreach (var game in Games)
            if (game is null || game.AppId == 0 || !ids.Add(game.AppId) || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 1024 || game.Name.Contains('\0'))
                throw new InvalidDataException("游戏库含无效或重复的游戏条目。");
    }
}
public sealed record LibraryAccess(string AccountId, bool Borrowed, bool? CloudEnabled);
public sealed record LibraryRow(uint AppId, string Name, bool Installed, List<LibraryAccess> Access, string? SuggestedAccountId, Game? Existing)
{
    public bool CanImport => Existing is null && SuggestedAccountId is not null;
}
public sealed record LibraryPreview(List<LibraryRow> Games, List<string> CapturedAccountIds, List<string> MissingAccountIds, List<string> Warnings)
{
    public int Importable => Games.Count(g => g.CanImport);
    public int Unresolved => Games.Count(g => g.Existing is null && g.Access.Count == 0);
}
public sealed record LibrarySelection(uint AppId, string AccountId);
public sealed record LibraryImportResult(Configuration Configuration, int Added, int Preserved);

public static class LibraryCatalog
{
    public static LibraryPreview Preview(Configuration config, IEnumerable<LibrarySnapshot> snapshots,
        IEnumerable<(uint AppId, string Name)> installed, DateTimeOffset now)
    {
        config.Validate();
        var current = new Dictionary<string, LibrarySnapshot>();
        var warnings = new List<string>();
        foreach (var snapshot in snapshots)
        {
            snapshot.Validate();
            var account = config.Accounts.SingleOrDefault(a => a.SteamId == snapshot.SteamId);
            if (account is null) continue;
            if (snapshot.CapturedAt > now.AddMinutes(5) || snapshot.CapturedAt < now.AddDays(-7))
            { warnings.Add($"{account.Name} 的游戏库记录已过期，请在普通 Steam 登录该账号刷新。"); continue; }
            if (!current.TryGetValue(account.Id, out var previous) || snapshot.CapturedAt > previous.CapturedAt)
                current[account.Id] = snapshot;
        }
        var installedGames = installed.DistinctBy(g => g.AppId).ToDictionary(g => g.AppId, g => g.Name);
        var all = new Dictionary<uint, (string Name, bool Installed, List<LibraryAccess> Access)>();
        foreach (var (accountId, snapshot) in current)
        foreach (var game in snapshot.Games)
        {
            if (!all.TryGetValue(game.AppId, out var row)) row = (game.Name, game.Installed, []);
            row.Installed |= game.Installed;
            if (game.Subscribed == true) row.Access.Add(new(accountId, game.OwnerAccountId != 0, game.CloudEnabled));
            all[game.AppId] = row;
        }
        foreach (var (id, name) in installedGames)
        {
            if (id == 228980) continue; // Steamworks shared redistributables are not a game.
            if (!all.TryGetValue(id, out var row)) row = (name, true, []);
            all[id] = (row.Name, true, row.Access);
        }
        foreach (var game in config.Games)
            if (!all.ContainsKey(game.AppId)) all[game.AppId] = (game.Name, installedGames.ContainsKey(game.AppId), []);
        var rows = all.Select(pair =>
        {
            var (name, isInstalled, access) = pair.Value;
            var existing = config.Games.SingleOrDefault(g => g.AppId == pair.Key);
            // Keep the user's selected account even if a different library is more convenient.
            var suggested = existing?.AccountId ?? access.FirstOrDefault(a => a.AccountId == config.DefaultNativeAccountId)?.AccountId ??
                access.OrderBy(a => a.Borrowed).ThenBy(a => a.AccountId, StringComparer.Ordinal).FirstOrDefault()?.AccountId;
            return new LibraryRow(pair.Key, existing?.Name ?? name, isInstalled, access, suggested, existing);
        }).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new(rows, current.Keys.ToList(), config.Accounts.Where(a => !current.ContainsKey(a.Id)).Select(a => a.Id).ToList(), warnings);
    }

    public static LibraryImportResult Import(Configuration config, LibraryPreview preview, IEnumerable<LibrarySelection>? selections = null)
    {
        var choices = selections?.ToList() ?? preview.Games.Where(g => g.CanImport).Select(g => new LibrarySelection(g.AppId, g.SuggestedAccountId!)).ToList();
        if (choices.Select(c => c.AppId).Distinct().Count() != choices.Count) throw new InvalidDataException("批量选择中存在重复游戏。");
        var games = config.Games.ToList();
        int added = 0, preserved = 0;
        foreach (var choice in choices)
        {
            var existing = games.SingleOrDefault(g => g.AppId == choice.AppId);
            if (existing is not null) { preserved++; continue; }
            var row = preview.Games.SingleOrDefault(g => g.AppId == choice.AppId) ?? throw new InvalidDataException("游戏不在当前导入列表中。");
            var access = row.Access.SingleOrDefault(a => a.AccountId == choice.AccountId) ??
                throw new InvalidDataException($"{row.Name} 没有该账号的可用许可记录；请手动核对后单独添加。");
            games.Add(new(row.AppId, row.Name, choice.AccountId)
            {
                Enabled = true, Mode = RunMode.Auto, AutoSwap = true,
                Saves = access.CloudEnabled == true ? SaveMode.SteamCloud : SaveMode.Unknown
            });
            added++;
        }
        var updated = config with { Games = games };
        updated.Validate();
        return new(updated, added, preserved);
    }
}
