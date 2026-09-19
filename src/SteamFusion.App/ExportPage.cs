using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SteamFusion.Core;
using SteamFusion.Windows;

namespace SteamFusion.App;

internal sealed class ExportPage : Grid
{
    private readonly ComboBox account = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly TextBox destination = new() { Text = Path.Combine(AppContext.BaseDirectory, "Exports") };
    private readonly TextBlock status = Ui.Text("点击“读取记录”查看本机数据覆盖情况。", 13, true);
    private readonly StackPanel preview = new();
    private readonly Button read, export, cancel, open;
    private CancellationTokenSource? operation;
    private string? lastDirectory;

    public ExportPage(Configuration config)
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new()); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(Ui.Heading("导出游戏时长与成就"));
        var intro = Ui.Text("读取普通 Steam 与沙盒中的本机记录，按账号分别导出。缓存缺失会标为未知，成就明细可能不完整。", 13, true);
        intro.Margin = new(0, 0, 0, 20); header.Children.Add(intro); Children.Add(header);
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new GridLength(340) }); content.ColumnDefinitions.Add(new() { Width = new GridLength(20) }); content.ColumnDefinitions.Add(new());
        SetRow(content, 1); Children.Add(content);
        var settings = new StackPanel(); settings.Children.Add(Ui.Heading("导出选项"));
        account.ItemsSource = new[] { new Option("", "全部账号（分别记录）") }.Concat(config.Accounts.Select(a => new Option(a.Id, a.Name))).ToList(); account.SelectedIndex = 0;
        settings.Children.Add(Ui.Field("账号", account));
        var folder = new DockPanel(); var browse = Ui.Button("浏览…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择导出保存位置", InitialDirectory = Directory.Exists(destination.Text) ? destination.Text : AppContext.BaseDirectory };
            if (dialog.ShowDialog() == true) destination.Text = dialog.FolderName;
        }); browse.Margin = new(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); folder.Children.Add(browse); folder.Children.Add(destination);
        settings.Children.Add(Ui.Field("保存位置", folder));
        System.Windows.Automation.AutomationProperties.SetName(destination, "导出保存位置");
        settings.Children.Add(Ui.Text("每次导出会创建独立文件夹：", 12, true));
        var files = Ui.Text("games.csv  ·  时长与成就汇总\nachievements.csv  ·  每条成就明细\ndata.json  ·  数据与来源记录\nREADME.txt  ·  数据完整性说明", 13, true); files.Margin = new(0, 12, 0, 20); files.LineHeight = 25; settings.Children.Add(files);
        var note = Ui.Text("时长不叠加普通与沙盒副本。文件更新时间不代表已实时同步；结束游戏并等待 Steam 同步后，可重新导出。", 12, true); settings.Children.Add(note);
        content.Children.Add(Ui.Card(Ui.Scroll(settings)));
        var results = new DockPanel(); var title = Ui.Heading("数据概览"); DockPanel.SetDock(title, Dock.Top); results.Children.Add(title);
        status.Margin = new(0, 0, 0, 16); DockPanel.SetDock(status, Dock.Top); results.Children.Add(status); results.Children.Add(Ui.Scroll(preview));
        var card = Ui.Card(results); SetColumn(card, 2); content.Children.Add(card);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 18, 0, 0) }; SetRow(actions, 2); Children.Add(actions);
        read = Ui.AsyncButton("读取记录", () => Run(false)); export = Ui.AsyncButton("导出 CSV＋JSON", () => Run(true)); export.SetResourceReference(StyleProperty, "PrimaryButton");
        cancel = Ui.Button("取消导出", () => operation?.Cancel()); cancel.IsEnabled = false;
        open = Ui.Button("打开导出文件夹", () => { if (lastDirectory is not null) Process.Start(new ProcessStartInfo(lastDirectory) { UseShellExecute = true }); }); open.IsEnabled = false;
        foreach (var button in new[] { read, export, cancel, open }) { button.Margin = new(0, 0, 8, 0); actions.Children.Add(button); }
        Unloaded += (_, _) => operation?.Cancel();
        account.SelectionChanged += (_, _) => { preview.Children.Clear(); status.Text = "账号已更改，点击“读取记录”查看数据。"; };
    }
    private async Task Run(bool write)
    {
        if (operation is not null) return;
        if (write && (string.IsNullOrWhiteSpace(destination.Text) || !Path.IsPathFullyQualified(destination.Text)))
            throw new InvalidOperationException("请选择完整的导出保存路径。");
        using var pending = new CancellationTokenSource(); operation = pending;
        read.IsEnabled = export.IsEnabled = account.IsEnabled = destination.IsEnabled = false; cancel.IsEnabled = true;
        var selected = account.SelectedValue as string; var directory = destination.Text.Trim();
        try
        {
            var config = File.Exists(Discovery.Store.ConfigPath) ? Discovery.Store.Load() : Discovery.Initial(Discovery.Scan());
            var progress = new Progress<string>(text => status.Text = text);
            var report = await Task.Run(() => GameDataExportService.Read(config, string.IsNullOrEmpty(selected) ? null : selected, progress, pending.Token), pending.Token);
            pending.Token.ThrowIfCancellationRequested(); ShowReport(report);
            if (write)
            {
                status.Text = "正在写入 CSV 和 JSON…";
                lastDirectory = await Task.Run(() => GameDataWriter.Write(report, directory, pending.Token), pending.Token);
                open.IsEnabled = true; status.Text = "导出完成。点击“打开导出文件夹”查看文件。";
            }
            else status.Text = "已读取本机缓存。未读取到的数据保持为空，详情见导出文件的完整性标记。";
        }
        catch (OperationCanceledException) { status.Text = "已取消，本次未完成的导出文件已清理。"; }
        catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
        finally { operation = null; read.IsEnabled = export.IsEnabled = account.IsEnabled = destination.IsEnabled = true; cancel.IsEnabled = false; }
    }
    private void ShowReport(GameDataReport report)
    {
        preview.Children.Clear();
        foreach (var data in report.Accounts)
        {
            var heading = Ui.Text(data.Name, 15); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 0, 0, 10); preview.Children.Add(heading);
            var hours = data.Games.Where(g => g.PlaytimeMinutes.HasValue).Sum(g => (decimal)g.PlaytimeMinutes!.Value) / 60m;
            var known = data.Games.Count(g => g.PlaytimeMinutes.HasValue);
            var summary = Ui.Text($"{data.Games.Count} 个游戏条目 · {known} 个有时长记录\n已知时长合计 {hours:N1} 小时\n{data.Games.Count(g => g.AchievementTotal.HasValue)} 个有成就汇总\n{data.Games.Sum(g => g.Achievements.Count)} 条成就明细 · {data.Games.Count(g => g.DetailCoverage == "complete-cache")} 个游戏缓存明细齐全", 13, true);
            summary.LineHeight = 25; summary.Margin = new(0, 0, 0, 20); preview.Children.Add(summary);
            if (data.Warnings.Count > 0)
            { var warning = Ui.Text($"{data.Warnings.Count} 条读取提示，导出文件中可查看详情。", 12, true); warning.Margin = new(0, 0, 0, 16); preview.Children.Add(warning); }
        }
    }
    private sealed record Option(string Id, string Name);
}
