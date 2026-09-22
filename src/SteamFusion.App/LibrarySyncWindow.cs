using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SteamFusion.Core;
using SteamFusion.Windows;

namespace SteamFusion.App;

public sealed class LibrarySyncWindow : Window
{
    private readonly Controller controller;
    private readonly LibrarySyncStore store = LibrarySyncStore.Current();
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly TextBox search = new() { MinWidth = 280, Margin = new(0, 0, 16, 0) };
    private readonly CheckBox showKept = new() { Content = "显示无需变更项", VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid table = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        RowHeight = 58, EnableRowVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Extended };
    private LibrarySyncPreview? preview;
    private List<LibrarySyncRow> rows = [];
    private ICollectionView? view;
    private readonly Button apply;
    public LibrarySyncWindow(Controller controller)
    {
        this.controller = controller;
        Title = "SteamFusion · 重新同步游戏库"; Width = 1200; Height = 800; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Apply(this);
        var panel = new DockPanel { Margin = new(24) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "重新核对两个账号的游戏库", FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "家庭变更后开始新一轮采集 → 两个账号分别在普通 Steam 加载库并等待约 1 分钟 → 刷新预览 → 勾选并应用。\n采集进度会保留，可以关闭此窗口去切换账号。沙盒内没有插件，不能采集。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0), Foreground = Ui.Brush("MutedBrush") });
        top.Children.Add(summary);
        var filters = new WrapPanel { Margin = new(0, 0, 0, 12) }; top.Children.Add(filters);
        filters.Children.Add(new TextBlock { Text = "搜索游戏 / AppID", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) });
        filters.Children.Add(search); filters.Children.Add(showKept);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "改账号和停用项默认不勾选。改账号后存档需重新核对，固定存档环境的游戏需手动处理。\n应用前自动备份配置；不移动存档、不卸载游戏。记录缺失、过期或许可未知时不会自动停用。", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("MutedBrush") });
        var actions = new WrapPanel { Margin = new(0, 12, 0, 0) }; footer.Children.Add(actions);
        void Button(string label, Action action) { var b = Ui.Button(label, action); b.Margin = new(0, 0, 8, 0); actions.Children.Add(b); }
        Button("开始新一轮采集", () =>
        {
            if (MessageBox.Show(this, "将以现在为起点重新采集。两个账号都需要在普通 Steam 刷新记录；现有游戏规则不会改变。", "开始新一轮", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            store.Begin(); Reload();
        });
        Button("刷新预览", Reload);
        Button("选择当前可应用项", () => SelectVisible(true));
        Button("清空选择", () => { foreach (var row in rows) row.Selected = false; });
        apply = Ui.Button("应用已勾选变更", Apply, true); actions.Children.Add(apply);
        var checkStyle = new Style(typeof(CheckBox), (Style)FindResource(typeof(CheckBox)));
        checkStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        checkStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(LibrarySyncRow.CanApply))));
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "应用", Width = 55,
            Binding = new Binding(nameof(LibrarySyncRow.Selected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            ElementStyle = checkStyle, EditingElementStyle = checkStyle });
        Column("游戏", nameof(LibrarySyncRow.Name), new(1, DataGridLengthUnitType.Star));
        Column("AppID", nameof(LibrarySyncRow.AppId), 85); Column("变更", nameof(LibrarySyncRow.Action), 90);
        Column("原账号 → 建议账号", nameof(LibrarySyncRow.Accounts), 240);
        var reasonStyle = new Style(typeof(TextBlock)); reasonStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        reasonStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(LibrarySyncRow.Reason))));
        table.Columns.Add(new DataGridTextColumn { Header = "说明", Binding = new Binding(nameof(LibrarySyncRow.Reason)), Width = new(1.5, DataGridLengthUnitType.Star), IsReadOnly = true, ElementStyle = reasonStyle });
        panel.Children.Add(table);
        search.TextChanged += (_, _) => view?.Refresh(); showKept.Checked += (_, _) => view?.Refresh(); showKept.Unchecked += (_, _) => view?.Refresh();
        Reload();
    }
    private void Column(string title, string property, DataGridLength width) => table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width, IsReadOnly = true });
    private void Reload()
    {
        preview = store.Preview(); var config = Discovery.Store.Load();
        rows = preview.Changes.Select(c => new LibrarySyncRow(c, config)).ToList();
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => item is LibrarySyncRow row && (showKept.IsChecked == true || row.Change.Action != LibrarySyncAction.Keep) &&
            (row.Name + " " + row.AppId + " " + row.Accounts).Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        table.ItemsSource = view; apply.IsEnabled = preview.Ready;
        summary.Text = "本轮开始：" + store.Session().StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss") + "\n" +
            string.Join("\n", preview.Accounts.Select(a => config.Accounts.Single(c => c.Id == a.AccountId).Name + "：" + a.Message +
                (a.CapturedAt is null ? "" : "（记录 " + a.CapturedAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss") + "）")));
        if (preview.Ready) summary.Text += $"\n新增 {Count(LibrarySyncAction.Add)} · 改账号 {Count(LibrarySyncAction.Reassign)} · 停用 {Count(LibrarySyncAction.Disable)} · 待核对 {Count(LibrarySyncAction.Review)} · 保留 {Count(LibrarySyncAction.Keep)}";
        else summary.Text += "\n两个账号的新记录齐备后才会生成变更建议。";
    }
    private int Count(LibrarySyncAction action) => preview!.Changes.Count(c => c.Action == action);
    private void SelectVisible(bool selected) { foreach (var row in view!.Cast<LibrarySyncRow>().Where(r => r.CanApply)) row.Selected = selected; }
    private void Apply()
    {
        table.CommitEdit(DataGridEditingUnit.Cell, true); table.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = rows.Where(r => r.Selected && r.CanApply).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "请先勾选需要应用的变更。"); return; }
        var moved = selected.Count(r => r.Change.Action == LibrarySyncAction.Reassign);
        var disabled = selected.Count(r => r.Change.Action == LibrarySyncAction.Disable);
        if (MessageBox.Show(this, $"应用 {selected.Count} 项变更，其中改账号 {moved} 项、停用 {disabled} 项？\n\n改账号不会迁移原账号存档，首次启动需要重新核对。将先备份原配置。", "确认游戏库变更", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            var result = controller.SynchronizeLibrary(preview!.Fingerprint, selected.Select(r => r.AppId));
            Reload(); summary.Text = $"已应用 {result.Changed} 项。备份：{result.BackupDirectory}\n" + summary.Text;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "同步未完成"); Reload(); }
    }
}

public sealed class LibrarySyncRow : INotifyPropertyChanged
{
    public LibrarySyncChange Change { get; }
    public uint AppId => Change.AppId;
    public string Name => Change.Name;
    public bool CanApply => Change.CanApply;
    public string Reason => Change.Reason;
    public string Accounts { get; }
    public string Action => Change.Action switch { LibrarySyncAction.Add => "新增", LibrarySyncAction.Reassign => "改账号", LibrarySyncAction.Disable => "停用", LibrarySyncAction.Review => "待核对", _ => "保留" };
    private bool selected;
    public bool Selected { get => selected; set { selected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public LibrarySyncRow(LibrarySyncChange change, Configuration config)
    {
        Change = change; selected = change.Action == LibrarySyncAction.Add;
        string NameOf(Game? game) => game is null ? "—" : config.Accounts.Single(a => a.Id == game.AccountId).Name;
        Accounts = NameOf(change.Before) + " → " + (change.Action == LibrarySyncAction.Disable ? "停用" : NameOf(change.After));
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
