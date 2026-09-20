using SteamFusion.Core;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;

namespace SteamFusion.Windows;

public sealed record Request(string Command, uint AppId = 0, string? AccountId = null, Guid RequestId = default);
public sealed record Reply(bool Success, string Code, string Message, string? Data = null);

public static class Ipc
{
    public static string UserKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确定 Windows 用户。"))))[..20];
    public static string PipeName => "SteamFusion-" + UserKey;
    public const int MaxMessage = 65536;

    public static async Task<Reply> SendAsync(Request request, bool startAgent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var pipe = CreateClient(PipeName);
        try { await pipe.ConnectAsync(250, timeout.Token); }
        catch (TimeoutException) when (startAgent)
        {
            var app = Path.Combine(AppContext.BaseDirectory, "SteamFusion.exe");
            if (!File.Exists(app)) throw new FileNotFoundException("发布目录缺少 SteamFusion.exe。", app);
            NativeMethods.StartAgent(app);
            await pipe.ConnectAsync(10000, timeout.Token);
        }
        ValidateServerOwner(pipe);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(Json.Encode(request).Replace("\r", "").Replace("\n", "").AsMemory(), timeout.Token);
        var text = await ReadLineBounded(reader, timeout.Token);
        return Json.Decode<Reply>(text);
    }

    public static async Task ServeAsync(Func<Request, Task<Reply>> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateServer(PipeName);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch { pipe.Dispose(); throw; }
            _ = HandleAsync(pipe, handler, ct);
        }
    }
    private static async Task HandleAsync(NamedPipeServerStream pipe, Func<Request, Task<Reply>> handler, CancellationToken ct)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var text = await ReadLineBounded(reader, timeout.Token);
                ValidateClientUser(pipe);
                var request = Json.Decode<Request>(text);
                var reply = await handler(request);
                await writer.WriteLineAsync(Json.Encode(reply).Replace("\r", "").Replace("\n", "").AsMemory(), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }
    private static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("无法确定当前 Windows 用户。");
    }
    // CurrentUserOnly compares token owners on Windows. An elevated CLI can have
    // Administrators as its owner while the desktop controller has the same USER SID.
    internal static NamedPipeClientStream CreateClient(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    internal static NamedPipeServerStream CreateServer(string name)
    {
        var user = CurrentUserSid();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }
    internal static void ValidateServerOwner(PipeStream pipe)
    {
        if (!CurrentUserSid().Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("后台连接不属于当前 Windows 用户，已拒绝发送请求。");
    }
    internal static void ValidateClientUser(NamedPipeServerStream pipe)
    {
        var expected = CurrentUserSid(); SecurityIdentifier? actual = null;
        try { pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); actual = identity?.User; }); }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException("无法验证请求的 Windows 用户，已拒绝执行。", ex);
        }
        if (actual is null || !expected.Equals(actual)) throw new UnauthorizedAccessException("请求不属于当前 Windows 用户，已拒绝执行。");
    }
    private static async Task<string> ReadLineBounded(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (result.Length <= MaxMessage)
        {
            if (await reader.ReadAsync(buffer, ct) == 0) throw new IOException("管道连接已关闭。");
            if (buffer[0] == '\n') return result.ToString();
            if (buffer[0] != '\r') result.Append(buffer[0]);
        }
        throw new InvalidDataException("请求长度超出限制。");
    }
}
