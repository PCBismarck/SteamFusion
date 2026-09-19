using System.Text;

namespace SteamFusion.Core;

public sealed class ConfigurationStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public Configuration Load()
    {
        var result = File.Exists(ConfigPath) ? Json.Decode<Configuration>(File.ReadAllText(ConfigPath)) : new Configuration();
        result.Validate();
        return result;
    }
    public void Save(Configuration config)
    {
        config.Validate();
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".bak", true);
        AtomicWrite(ConfigPath, Json.Encode(config));
    }
    public static void AtomicWrite(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, contents, new UTF8Encoding(false)); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

/// <summary>Text VDF reader. Only selected public metadata is exposed by callers.</summary>
public sealed class Vdf
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vdf> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Get(string key, string fallback = "") => Values.GetValueOrDefault(key, fallback);
    public static Vdf Parse(string input)
    {
        var offset = 0;
        string? Token()
        {
            while (offset < input.Length)
            {
                if (char.IsWhiteSpace(input[offset])) { offset++; continue; }
                if (input[offset] == '/' && offset + 1 < input.Length && input[offset + 1] == '/')
                { while (offset < input.Length && input[offset] != '\n') offset++; continue; }
                break;
            }
            if (offset >= input.Length) return null;
            var c = input[offset++];
            if (c is '{' or '}') return c.ToString();
            var sb = new StringBuilder();
            if (c == '"')
            {
                while (offset < input.Length)
                {
                    c = input[offset++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\' && offset < input.Length && input[offset] is '\\' or '"') c = input[offset++];
                    sb.Append(c);
                }
                throw new InvalidDataException("VDF 引号未闭合。");
            }
            sb.Append(c);
            while (offset < input.Length && !char.IsWhiteSpace(input[offset]) && input[offset] is not '{' and not '}') sb.Append(input[offset++]);
            return sb.ToString();
        }
        Vdf Read(int depth, bool nested)
        {
            if (depth > 32) throw new InvalidDataException("VDF 嵌套过深。");
            var node = new Vdf();
            while (Token() is { } key)
            {
                if (key == "}") { if (!nested) throw new InvalidDataException("多余 VDF 括号。"); return node; }
                var value = Token() ?? throw new InvalidDataException("VDF 值缺失。");
                if (value == "{") node.Children[key] = Read(depth + 1, true);
                else if (value == "}") throw new InvalidDataException("VDF 值缺失。");
                else node.Values[key] = value;
            }
            if (nested) throw new InvalidDataException("VDF 括号未闭合。");
            return node;
        }
        return Read(0, false);
    }
}
