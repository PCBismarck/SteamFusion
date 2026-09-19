using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SteamFusion.Windows;

public static class WattStore
{
    public const string Setting = "store:WattToolkit";
    public const string AppId = "4651ED44255E.47979655102CE_k6txddmbb6c52!App";
    public static bool Installed()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return packages?.GetSubKeyNames().Any(n => n.StartsWith("4651ED44255E.47979655102CE_", StringComparison.OrdinalIgnoreCase) &&
            n.EndsWith("_k6txddmbb6c52", StringComparison.OrdinalIgnoreCase)) == true;
    }
    public static uint Activate(params string[] arguments)
    {
        if (!Installed()) throw new InvalidOperationException("当前 Windows 用户未安装商店版 Watt Toolkit。");
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            manager.ActivateApplication(AppId, string.Join(' ', arguments.Select(NativeMethods.Quote)), 0, out var pid);
            return pid;
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
}
