using System.Text;

namespace SteamFusion.Core;

public sealed record BinaryEntry(byte Type, string Key, object Value);
public static class BinaryVdf
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static List<BinaryEntry> Read(byte[] bytes)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("快捷方式文件过大。");
        using var reader = new BinaryReader(new MemoryStream(bytes), Utf8);
        List<BinaryEntry> Parse(int depth)
        {
            if (depth > 32) throw new InvalidDataException("快捷方式嵌套过深。");
            var result = new List<BinaryEntry>();
            while (true)
            {
                var type = reader.ReadByte();
                if (type == 8) return result;
                var key = ReadString(reader);
                object value = type switch
                {
                    0 => Parse(depth + 1),
                    1 => ReadString(reader),
                    2 or 3 or 4 => ReadExact(reader, 4),
                    7 => ReadExact(reader, 8),
                    _ => throw new InvalidDataException($"未知的快捷方式字段类型 {type}，未修改文件。")
                };
                result.Add(new(type, key, value));
            }
        }
        try
        {
            var parsed = Parse(0);
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("快捷方式文件含有额外数据，未修改。");
            return parsed;
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("快捷方式文件不完整，未修改。", ex); }
    }
    public static byte[] Write(List<BinaryEntry> entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        void String(string value)
        {
            if (value.Contains('\0')) throw new InvalidDataException("快捷方式文本不能包含 NUL。");
            writer.Write(Utf8.GetBytes(value)); writer.Write((byte)0);
        }
        void Serialize(List<BinaryEntry> nodes)
        {
            foreach (var entry in nodes)
            {
                writer.Write(entry.Type); String(entry.Key);
                if (entry.Type == 0) Serialize((List<BinaryEntry>)entry.Value);
                else if (entry.Type == 1) String((string)entry.Value);
                else writer.Write((byte[])entry.Value);
            }
            writer.Write((byte)8);
        }
        Serialize(entries); return stream.ToArray();
    }
    private static string ReadString(BinaryReader reader)
    {
        var result = new List<byte>(); byte b;
        while ((b = reader.ReadByte()) != 0)
        { result.Add(b); if (result.Count > 1024 * 1024) throw new InvalidDataException("字符串过长。"); }
        return Utf8.GetString(result.ToArray());
    }
    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var result = reader.ReadBytes(length);
        return result.Length == length ? result : throw new EndOfStreamException();
    }
    public static uint ShortcutId(string quotedExe, string name)
    {
        uint crc = 0xffffffff;
        foreach (var b in Utf8.GetBytes(quotedExe + name))
        {
            crc ^= b;
            for (var n = 0; n < 8; n++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0);
        }
        return ~crc | 0x80000000;
    }
}
