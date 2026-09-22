using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SteamFusion.Core;
using SteamFusion.Windows;

namespace SteamFusion.App;

public sealed class LibraryImportWindow : Window
{
    private readonly Controller controller;
    private readonly TextBox search = new() { Padding = new(9), Margin = new(0, 12, 0, 12) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly DataGrid table = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Extended, RowHeight = 48,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        EnableRowVirtualization = true };
    private List<LibraryImportRow> rows = [];
    private ICollectionView? view;

    public LibraryImportWindow(Controller controller)
    {
        this.controller = controller;
        Title = "SteamFusion · 批量导入游戏库"; Width = 1120; Height = 760; MinWidth = 850; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.Apply(this);
        var panel = new DockPanel { Margin = new(24) }; Content = panel;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "导入两个账号的游戏库", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(summary);
        header.Children.Add(new TextBlock { Text = "按游戏名称、AppID 或账号搜索", Foreground = Brushes.LightSteelBlue });
        header.Children.Add(search);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "只新增规则，保留已有账号、运行方式和存档设置。新增游戏默认自动路由；云存档未确认时，跨环境启动会提示核对。\n没有许可记录的游戏不自动绑定；可回到设置手动核对并添加。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, FontSize = 12 });
        var actions = new WrapPanel { Margin = new(0, 12, 0, 0) }; footer.Children.Add(actions);
        actions.Children.Add(Action("选择可导入项", () => SetVisible(true)));
        actions.Children.Add(Action("清空当前选择", () => SetVisible(false)));
        actions.Children.Add(Action("刷新读取", Reload));
        actions.Children.Add(Action("重新同步游戏库", () => { new LibrarySyncWindow(controller) { Owner = this }.ShowDialog(); Reload(); }));
        actions.Children.Add(Action("导入已勾选游戏", Apply));
        actions.Children.Add(Action("关闭", Close));
        var checkStyle = new Style(typeof(CheckBox), (Style)FindResource(typeof(CheckBox)));
        checkStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        checkStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(LibraryImportRow.CanImport))));
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "导入", Binding = new Binding(nameof(LibraryImportRow.Selected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            ElementStyle = checkStyle, EditingElementStyle = checkStyle, Width = 55 });
        AddColumn("游戏", nameof(LibraryImportRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn("AppID", nameof(LibraryImportRow.AppId), 90);
        var factory = new FrameworkElementFactory(typeof(ComboBox));
        factory.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryImportRow.Options)));
        factory.SetValue(ComboBox.DisplayMemberPathProperty, nameof(LibraryAccountOption.Name));
        factory.SetValue(ComboBox.SelectedValuePathProperty, nameof(LibraryAccountOption.Id));
        factory.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(LibraryImportRow.AccountId)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        factory.SetValue(ComboBox.MinHeightProperty, 32d);
        factory.SetBinding(IsEnabledProperty, new Binding(nameof(LibraryImportRow.CanImport)));
        table.Columns.Add(new DataGridTemplateColumn { Header = "游玩账号", Width = 270, CellTemplate = new DataTemplate { VisualTree = factory } });
        AddColumn("状态", nameof(LibraryImportRow.State), 140);
        AddColumn("安装", nameof(LibraryImportRow.Installation), 70);
        panel.Children.Add(table);
        search.TextChanged += (_, _) => view?.Refresh();
        Reload();
    }
    private void Reload()
    {
        var config = Discovery.Store.Load();
        var preview = CatalogStore.Preview(config);
        rows = preview.Games.Select(g => new LibraryImportRow(g, config)).ToList();
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => item is LibraryImportRow row &&
            (search.Text.Length == 0 || (row.Name + " " + row.AppId + " " + string.Join(' ', row.Options.Select(o => o.Name)))
                .Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        table.ItemsSource = view;
        summary.Text = $"已读取 {preview.CapturedAccountIds.Count}/{config.Accounts.Count} 个账号 · {preview.Games.Count} 个条目 · 可新增 {preview.Importable} 个 · 待核对 {preview.Unresolved} 个。";
        if (preview.MissingAccountIds.Count > 0)
            summary.Text += "\n尚未采集：" + string.Join("、", config.Accounts.Where(a => preview.MissingAccountIds.Contains(a.Id)).Select(a => a.Name)) + "。请在普通 Steam 登录对应账号，插件会自动刷新游戏库。";
        if (preview.Warnings.Count > 0) summary.Text += "\n" + string.Join("\n", preview.Warnings);
    }
    private void SetVisible(bool selected)
    { foreach (var row in view!.Cast<LibraryImportRow>().Where(r => r.CanImport)) row.Selected = selected; }
    private void Apply()
    {
        table.CommitEdit(DataGridEditingUnit.Cell, true); table.CommitEdit(DataGridEditingUnit.Row, true);
        var selection = rows.Where(r => r.Selected && r.CanImport).Select(r => new LibrarySelection(r.AppId, r.AccountId!)).ToList();
        if (selection.Count == 0) { summary.Text = "请先勾选可导入的游戏。"; return; }
        var result = controller.ImportLibrary(selection);
        Reload(); summary.Text = $"已新增 {result.Added} 个游戏规则；已有规则保留。\n" + summary.Text;
    }
    private void AddColumn(string name, string path, DataGridLength width) => table.Columns.Add(new DataGridTextColumn
    { Header = name, Binding = new Binding(path), Width = width, IsReadOnly = true });
    private static Button Action(string text, System.Action action)
    {
        var button = Ui.Button(text, action, text == "导入已勾选游戏");
        button.Margin = new(0, 0, 8, 0); return button;
    }
}
public sealed record LibraryAccountOption(string Id, string Name);
public sealed class LibraryImportRow : INotifyPropertyChanged
{
    public uint AppId { get; }
    public string Name { get; }
    public string State { get; }
    public string Installation { get; }
    public bool CanImport { get; }
    public List<LibraryAccountOption> Options { get; }
    public string? AccountId { get; set; }
    private bool selected;
    public bool Selected { get => selected; set { selected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public LibraryImportRow(LibraryRow row, Configuration config)
    {
        AppId = row.AppId; Name = row.Name; CanImport = row.CanImport; selected = CanImport; AccountId = row.SuggestedAccountId;
        Installation = row.Installed ? "已安装" : "未安装";
        State = row.Existing is not null ? row.Existing.Enabled ? "已配置 · 保留" : "已停用 · 保留" : CanImport ? "可导入" : "归属待核对";
        Options = row.Access.Select(a => new LibraryAccountOption(a.AccountId,
            config.Accounts.Single(c => c.Id == a.AccountId).Name + (a.Borrowed ? "（家庭共享）" : ""))).ToList();
        if (row.Existing is not null && !Options.Any(o => o.Id == row.Existing.AccountId))
            Options.Add(new(row.Existing.AccountId, config.Accounts.Single(a => a.Id == row.Existing.AccountId).Name));
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
