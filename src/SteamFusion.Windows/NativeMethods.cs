using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamFusion.Windows;

public static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr window, string text, string title, uint type);
    public static void NotifyError(string text) => MessageBoxW(IntPtr.Zero, text, "SteamFusion", 0x10);
    public static void RequireOutsideSandbox()
    {
        // A sandbox-injected SbieDll is already loaded even when tool paths are not configured.
        var dll = GetModuleHandleW("SbieDll.dll");
        if (dll != IntPtr.Zero)
            throw new InvalidOperationException("请先在普通 Windows 桌面启动 SteamFusion.exe。沙盒内的入口只连接外部控制器；请检查 Sandboxie 的 OpenPipePath 配置。");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string module);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startupInfo, out ProcessInfo processInfo);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2; public IntPtr reservedPtr, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    public static void StartAgent(string exe)
    {
        RequireOutsideSandbox();
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        // Break out of Steam's job if one exists; this process must survive Steam's shutdown.
        var command = new StringBuilder(Quote(exe) + " --agent");
        if (!CreateProcessW(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x01000008, IntPtr.Zero,
            Path.GetDirectoryName(exe)!, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动独立后台控制器。请先双击 SteamFusion.exe 启动设置窗口。");
        CloseHandle(process.thread); CloseHandle(process.process);
    }

    public static string Quote(string value)
    {
        var output = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { output.Append('\\', slashes * 2 + 1); output.Append('"'); }
            else { output.Append('\\', slashes); output.Append(c); }
            slashes = 0;
        }
        output.Append('\\', slashes * 2); output.Append('"');
        return output.ToString();
    }
}

public sealed class SandboxApi : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryProcess(IntPtr pid, StringBuilder box, StringBuilder image, StringBuilder sid, out uint session);
    private readonly IntPtr library;
    private readonly QueryProcess? query;
    public bool Available => query is not null;
    public SandboxApi(string startExe)
    {
        var dll = Path.Combine(Path.GetDirectoryName(startExe) ?? "", "SbieDll.dll");
        if (Path.IsPathFullyQualified(dll) && File.Exists(dll))
        {
            library = NativeLibrary.Load(dll);
            query = Marshal.GetDelegateForFunctionPointer<QueryProcess>(NativeLibrary.GetExport(library, "SbieApi_QueryProcess"));
        }
    }
    public string? GetBox(int pid)
    {
        if (query is null) return null;
        var box = new StringBuilder(34); var image = new StringBuilder(96); var sid = new StringBuilder(96);
        var status = query((IntPtr)pid, box, image, sid, out _);
        // STATUS_INVALID_CID: a live ordinary process is not sandboxed.
        if (status == unchecked((int)0xc000000b)) return null;
        if (status != 0) throw new InvalidOperationException($"无法确认进程 {pid} 的沙盒归属：0x{status:x8}");
        return box.Length == 0 ? null : box.ToString();
    }
    public void Dispose() { if (library != IntPtr.Zero) NativeLibrary.Free(library); }
}
