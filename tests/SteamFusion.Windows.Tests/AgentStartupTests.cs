using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SteamFusion.Windows;

internal static class AgentStartupTests
{
    private const string DirectoryVariable = "STEAMFUSION_STARTUP_TEST_DIRECTORY";
    public static int Agent()
    {
        var root = Environment.GetEnvironmentVariable(DirectoryVariable) ?? throw new InvalidOperationException("Missing fixture directory.");
        File.WriteAllText(Path.Combine(root, "agent.tmp"), JsonSerializer.Serialize(new { pid = Environment.ProcessId }));
        File.Move(Path.Combine(root, "agent.tmp"), Path.Combine(root, "agent.json"));
        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until && !File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(50);
        return 0;
    }
    public static int Worker()
    {
        var root = Environment.GetEnvironmentVariable(DirectoryVariable)!;
        try
        {
            if (!IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var inJob) || !inJob) throw new Exception("Worker must be in the restricted job.");
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (CreateProcessW(Environment.ProcessPath!, new StringBuilder(NativeMethods.Quote(Environment.ProcessPath!) + " --agent"), IntPtr.Zero, IntPtr.Zero, false,
                0x01000008, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var unexpected))
            { CloseHandle(unexpected.thread); CloseHandle(unexpected.process); throw new Exception("Old breakaway path unexpectedly succeeded."); }
            var error = Marshal.GetLastWin32Error();
            if (error != 5) throw new Exception("Expected original startup error 5; got " + error);
            File.WriteAllText(Path.Combine(root, "original-error.txt"), error.ToString());
            NativeMethods.StartAgent(Environment.ProcessPath!);
            File.WriteAllText(Path.Combine(root, "worker-ok"), "ok");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "worker-error.txt"), ex.ToString()); return 1; }
    }
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamFusion startup 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(DirectoryVariable, root);
        var job = CreateJobObjectW(IntPtr.Zero, null);
        Process? agent = null, worker = null;
        try
        {
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { basic = new BasicLimits { flags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway allowed.
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(Environment.ProcessPath!, new StringBuilder(NativeMethods.Quote(Environment.ProcessPath!) + " --startup-job-worker"), IntPtr.Zero, IntPtr.Zero,
                false, 4 | 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var child)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                worker = Process.GetProcessById((int)child.pid);
                if (!AssignProcessToJobObject(job, child.process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (ResumeThread(child.thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseHandle(child.thread); CloseHandle(child.process); }
            if (!worker.WaitForExit(15000)) throw new Exception("Worker did not finish.");
            if (!File.Exists(Path.Combine(root, "worker-ok"))) throw new Exception(File.ReadAllText(Path.Combine(root, "worker-error.txt")));
            WaitFor(() => File.Exists(Path.Combine(root, "agent.json")), "Agent did not start.");
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "agent.json")));
            agent = Process.GetProcessById(result.RootElement.GetProperty("pid").GetInt32());
            if (!IsProcessInJob(agent.Handle, job, out var inJob) || inJob) throw new Exception("Agent inherited the plugin's job.");
            CloseHandle(job); job = IntPtr.Zero;
            Thread.Sleep(250);
            if (agent.HasExited) throw new Exception("Agent did not survive job closure.");
            File.WriteAllText(Path.Combine(root, "stop"), "stop");
            if (!agent.WaitForExit(5000)) throw new Exception("Fixture did not exit normally.");
            try { NativeMethods.StartAgent(Path.Combine(root, "missing.exe")); throw new Exception("Missing executable was accepted."); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
            { if (!ex.Message.Contains("Win32")) throw new Exception("Diagnostic omitted the native error code."); }
            Console.WriteLine("PASS startup: original breakaway fails with Win32 5; desktop fallback starts outside the restricted job and survives job closure; missing executable reports native error.");
            return 0;
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "stop"), "stop");
            if (job != IntPtr.Zero) CloseHandle(job);
            if (agent is not null && !agent.HasExited) agent.WaitForExit(5000);
            agent?.Dispose(); worker?.Dispose(); Environment.SetEnvironmentVariable(DirectoryVariable, null);
            Directory.Delete(root, true);
        }
    }
    private static void WaitFor(Func<bool> check, string error)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!check()) { if (DateTime.UtcNow >= until) throw new Exception(error); Thread.Sleep(50); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fill, flags;
        public short show, reserved2; public IntPtr reservedPtr, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long processTime, jobTime; public uint flags; public UIntPtr minWorking, maxWorking; public uint activeLimit; public UIntPtr affinity; public uint priority, scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong readOps, writeOps, otherOps, readBytes, writeBytes, otherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits basic; public IoCounters io; public UIntPtr processMemory, jobMemory, peakProcess, peakJob; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string exe, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr env, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
