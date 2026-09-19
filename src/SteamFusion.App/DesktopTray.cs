using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SteamFusion.App;

/// <summary>One notification-area entry for the single background controller.</summary>
internal sealed class DesktopTray : IDisposable
{
    private readonly Application application;
    private readonly Controller controller;
    private readonly Drawing.Icon icon;
    private readonly Drawing.Font font;
    private readonly Forms.NotifyIcon notification;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem state;
    private bool closeHintShown;
    private bool disposed;

    public DesktopTray(Application application, Controller controller)
    {
        this.application = application; this.controller = controller;
        // Read the icon embedded in the EXE. WPF's resource lookup can classify the
        // separately published Assets file as Content and return no Resource stream.
        icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        font = new Drawing.Font("Microsoft YaHei UI", 9f);
        menu = new Forms.ContextMenuStrip
        {
            Font = font, ShowImageMargin = false, ShowCheckMargin = false,
            BackColor = Drawing.Color.FromArgb(29, 39, 51), ForeColor = Drawing.Color.FromArgb(234, 241, 247),
            Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors())
        };
        state = new Forms.ToolStripMenuItem("SteamFusion · 后台运行中") { Enabled = false };
        menu.Items.Add(state); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 SteamFusion", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => state.Text = controller.Busy ? "SteamFusion · 正在执行操作" : "SteamFusion · 后台运行中";
        notification = new Forms.NotifyIcon
        {
            Icon = icon, Text = "SteamFusion · 双击打开设置", ContextMenuStrip = menu, Visible = true
        };
        notification.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenSettings(); };
        notification.BalloonTipClicked += (_, _) => OpenSettings();
        controller.SettingsClosed += OnSettingsClosed;
    }
    private void OpenSettings()
    {
        if (!disposed && !controller.IsExiting)
            application.Dispatcher.BeginInvoke(controller.ShowSettings);
    }
    private void OnSettingsClosed()
    {
        if (disposed || closeHintShown || controller.IsExiting) return;
        closeHintShown = true;
        notification.ShowBalloonTip(4000, "SteamFusion 仍在后台运行",
            "双击托盘图标打开设置，右键可退出。找不到图标时，请展开任务栏右下角的隐藏图标。", Forms.ToolTipIcon.Info);
    }
    private async Task ExitAsync()
    {
        if (disposed || controller.IsExiting) return;
        try
        {
            // Reuse the existing guard: an active launch/swap must finish or be cancelled first.
            var reply = await controller.Handle(new("exit"));
            if (!reply.Success) MessageBox.Show(reply.Message, "SteamFusion", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "SteamFusion", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; controller.SettingsClosed -= OnSettingsClosed;
        notification.Visible = false; notification.Dispose(); menu.Dispose(); icon.Dispose(); font.Dispose();
    }
    private sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Drawing.Color.FromArgb(29, 39, 51);
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(51, 66, 83);
        public override Drawing.Color MenuItemBorder => Drawing.Color.FromArgb(103, 200, 245);
        public override Drawing.Color MenuItemSelected => Drawing.Color.FromArgb(41, 70, 92);
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(51, 66, 83);
        public override Drawing.Color SeparatorLight => SeparatorDark;
    }
}
