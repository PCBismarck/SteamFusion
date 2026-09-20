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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessExtended(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfoEx startupInfo, out ProcessInfo processInfo);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2; public IntPtr reservedPtr, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo startup; public IntPtr attributes; }
    public static void StartAgent(string exe)
    {
        RequireOutsideSandbox();
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        // The controller must survive Steam/plugin shutdown. Some Millennium backend
        // jobs disallow breakaway: inherit the interactive shell's job/token instead.
        var command = new StringBuilder(Quote(exe) + " --agent");
        if (!CreateProcessW(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x01000008, IntPtr.Zero,
            Path.GetDirectoryName(exe)!, ref startup, out var process))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 5 && IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) && inJob)
            {
                try { StartAgentFromDesktop(exe); return; }
                catch (Win32Exception ex)
                {
                    throw new Win32Exception(ex.NativeErrorCode, $"无法通过桌面启动后台控制器：{ex.Message}（Win32 {ex.NativeErrorCode}）。请双击发布目录的 SteamFusion 快捷方式后重试。");
                }
            }
            throw new Win32Exception(error, $"无法启动后台控制器：{new Win32Exception(error).Message}（Win32 {error}）。请双击发布目录的 SteamFusion 快捷方式后重试。");
        }
        CloseHandle(process.thread); CloseHandle(process.process);
    }

    private static void StartAgentFromDesktop(string exe)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new Win32Exception(1168, "当前 Windows 桌面尚未就绪");
        if (GetWindowThreadProcessId(shell, out var pid) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parent = OpenProcess(0x0080, false, pid); // PROCESS_CREATE_PROCESS, no elevation or handle inheritance.
        if (parent == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr attributes = IntPtr.Zero, parentValue = IntPtr.Zero;
        var initialized = false;
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            parentValue = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(parentValue, parent);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020000, parentValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx { startup = new() { cb = Marshal.SizeOf<StartupInfoEx>() }, attributes = attributes };
            if (!CreateProcessExtended(exe, new StringBuilder(Quote(exe) + " --agent"), IntPtr.Zero, IntPtr.Zero, false,
                0x00080008, IntPtr.Zero, Path.GetDirectoryName(exe)!, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(process.thread); CloseHandle(process.process);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (parentValue != IntPtr.Zero) Marshal.FreeHGlobal(parentValue);
            CloseHandle(parent);
        }
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
