using System.Text;

namespace SteamFusion.Core;

/// <summary>Edits only selection flags, retaining all unrelated text and fields.</summary>
public static class SteamLoginUsers
{
    private sealed record Token(string Text, int Start, int Length, char Kind);
    private sealed record Entry(Token Key, Token? Value, Node? Child);
    private sealed record Node(Dictionary<string, Entry> Entries, int Close);

    public static string Select(string text, Account account)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Steam 登录账号文件过大，未修改。");
        new Configuration { Accounts = [account] }.Validate();
        var offset = 0;
        Token? Next()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]) || offset == 0 && text[offset] == '\uFEFF') { offset++; continue; }
                if (text[offset] == '/' && offset + 1 < text.Length && text[offset + 1] == '/')
                { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                break;
            }
            if (offset == text.Length) return null;
            int start = offset;
            char c = text[offset++];
            if (c is '{' or '}') return new(c.ToString(), start, 1, c);
            var value = new StringBuilder();
            if (c == '"')
            {
                while (offset < text.Length)
                {
                    c = text[offset++];
                    if (c == '"') return new(value.ToString(), start, offset - start, 'v');
                    if (c == '\\' && offset < text.Length && text[offset] is '\\' or '"') c = text[offset++];
                    value.Append(c);
                }
                throw new InvalidDataException("Steam 登录账号文件引号未闭合，未修改。");
            }
            value.Append(c);
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not '{' and not '}') value.Append(text[offset++]);
            return new(value.ToString(), start, offset - start, 'v');
        }
        Node Read(int depth)
        {
            if (depth > 32) throw new InvalidDataException("Steam 登录账号文件嵌套过深，未修改。");
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            while (Next() is { } key)
            {
                if (key.Kind == '}' && depth > 0) return new(entries, key.Start);
                if (key.Kind != 'v') throw new InvalidDataException("Steam 登录账号文件结构异常，未修改。");
                var value = Next() ?? throw new InvalidDataException("Steam 登录账号文件值缺失，未修改。");
                if (value.Kind == '}') throw new InvalidDataException("Steam 登录账号文件值缺失，未修改。");
                var entry = value.Kind == '{' ? new Entry(key, null, Read(depth + 1)) : new Entry(key, value, null);
                if (!entries.TryAdd(key.Text, entry)) throw new InvalidDataException("Steam 登录账号文件包含重复字段，未修改。");
            }
            if (depth > 0) throw new InvalidDataException("Steam 登录账号文件括号未闭合，未修改。");
            return new(entries, text.Length);
        }
        var root = Read(0);
        var users = root.Entries.GetValueOrDefault("users")?.Child
            ?? throw new InvalidDataException("找不到 Steam 已记住的账号，未修改。");
        var target = users.Entries.GetValueOrDefault(account.SteamId)?.Child;
        if (target?.Entries.GetValueOrDefault("AccountName")?.Value is not { } login ||
            !string.Equals(login.Text, account.LoginName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目标账号不在 Steam 已记住的账号中，或用户名与 SteamID 不一致。请先在普通 Steam 登录一次。");
        if (users.Entries.Values.Any(e => e.Child is null))
            throw new InvalidDataException("Steam 登录账号文件结构异常，未修改。");
        var edits = new List<(int Start, int Length, string Text)>();
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        void Flag(Node node, string key, string value)
        {
            if (node.Entries.TryGetValue(key, out var entry))
            {
                if (entry.Value is null) throw new InvalidDataException("Steam 登录账号标记格式异常，未修改。");
                if (entry.Value.Text != value) edits.Add((entry.Value.Start, entry.Value.Length, "\"" + value + "\""));
            }
            else edits.Add((node.Close, 0, newline + "\t\t\"" + key + "\"\t\t\"" + value + "\"" + newline + "\t"));
        }
        foreach (var entry in users.Entries.Values) Flag(entry.Child!, "MostRecent", entry.Child == target ? "1" : "0");
        Flag(target!, "RememberPassword", "1");
        var result = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start)) result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        return result.ToString();
    }
}
