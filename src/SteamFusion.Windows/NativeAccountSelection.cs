using Microsoft.Win32;
using SteamFusion.Core;
using System.Text;

namespace SteamFusion.Windows;

public static class NativeAccountSelection
{
    // The caller supplies a live process check; tests use an isolated registry key and files.
    public static void Select(string steamExe, Account account, RegistryKey steamKey, Action ensureStopped)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(steamExe))!, "config", "loginusers.vdf");
        ensureStopped();
        var original = File.ReadAllBytes(path);
        var utf8 = new UTF8Encoding(false, true);
        var selected = utf8.GetBytes(SteamLoginUsers.Select(utf8.GetString(original), account));
        var previous = new[] { "AutoLoginUser", "RememberPassword" }.Select(name =>
            (Name: name, Value: steamKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                Kind: steamKey.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ? steamKey.GetValueKind(name) : (RegistryValueKind?)null)).ToArray();
        var suffix = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        var backup = path + ".steamfusion-" + suffix + ".bak";
        var temporary = path + ".steamfusion-" + suffix + ".tmp";
        bool replaced = false;
        int registryWrites = 0;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(selected); stream.Flush(true); }
            ensureStopped();
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new IOException("Steam 登录账号文件在准备切换时发生变化，请重试。");
            File.Replace(temporary, path, backup);
            replaced = true;
            ensureStopped();
            steamKey.SetValue("AutoLoginUser", account.LoginName, RegistryValueKind.String);
            registryWrites++;
            steamKey.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
            registryWrites++;
            steamKey.Flush();
        }
        catch (Exception failure)
        {
            try
            {
                if (replaced)
                {
                    // Never overwrite files now owned by a newly launched Steam process.
                    ensureStopped();
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(selected))
                        throw new IOException("账号文件再次变化，保留备份供手动恢复。");
                    if (registryWrites > 0)
                        foreach (var old in previous.Take(registryWrites).Reverse())
                            if (old.Kind is { } kind) steamKey.SetValue(old.Name, old.Value!, kind);
                            else steamKey.DeleteValue(old.Name, false);
                    File.Copy(backup, temporary, true);
                    File.Replace(temporary, path, null);
                }
            }
            catch (Exception rollback)
            { throw new IOException($"切号准备失败且无法自动恢复。原账号文件备份：{backup}。请检查 Steam 状态后重试。", new AggregateException(failure, rollback)); }
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
