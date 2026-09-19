using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SteamFusion.Core;
using SteamFusion.Windows;

namespace SteamFusion.App;

public sealed class SettingsWindow : Window
{
    private readonly Controller controller;
    private Configuration config;
    private readonly DiscoveryReport scan;
    private readonly TextBox steam = new(), sandbox = new(), search = new();
    private readonly ComboBox native = new(), account = new(), mode = new(), saves = new();
    private readonly ListBox selectedGame = new() { SelectedValuePath = "Value" };
    private readonly CheckBox enabled = new() { Content = "确认账号拥有游玩权限，启用此规则" };
    private readonly CheckBox automatic = new() { Content = "必须在普通客户端运行时，允许自动切号" };
    private readonly CheckBox dual = new() { Content = "切号后，让另一个账号继续在沙盒中登录" };
    private readonly CheckBox integrate = new() { Content = "自动添加非 Steam 备用入口" };
    private readonly CheckBox uninstalled = new() { Content = "在 Steam 插件中显示另一账号的未安装游戏页" };
    private readonly TextBlock status = Ui.Text("", 12, true), counts = Ui.Text("", 12, true), listCount = Ui.Text("", 12, true);
    private readonly TextBlock gameTitle = Ui.Heading("选择一个游戏"), gameMeta = Ui.Text("", 12, true);
    private readonly StackPanel editor = new();
    private readonly WrapPanel gameActions = new();
    private ICollectionView? gamesView;
    private List<GameItem> gameItems = [];
    private bool refreshing;

    public SettingsWindow(Controller controller)
    {
        this.controller = controller;
        scan = Discovery.Scan();
        config = File.Exists(Discovery.Store.ConfigPath) ? Discovery.Store.Load() : Discovery.Initial(scan);
        Ui.Apply(this);
        Title = "SteamFusion · 设置"; Width = 1120; Height = 800; MinWidth = 980; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        steam.Text = config.SteamExe; sandbox.Text = config.SandboxieStartExe;
        foreach (var choice in new[] { native, account })
        { choice.ItemsSource = config.Accounts; choice.DisplayMemberPath = "Name"; choice.SelectedValuePath = "Id"; }
        native.SelectedValue = config.DefaultNativeAccountId;
        dual.IsChecked = config.KeepBothOnline; integrate.IsChecked = config.IntegrateLibrary; uninstalled.IsChecked = config.UninstalledLibrary;
        mode.ItemsSource = new[] { new Item("按账号位置自动路由", "auto"), new Item("必须使用普通客户端", "native") };
        saves.ItemsSource = new[] { new Item("尚未确认 · 跨环境时提醒", "unknown"), new Item("使用 Steam 云存档", "cloud"), new Item("固定在普通环境", "native"), new Item("固定在该账号沙盒", "sandbox") };
        foreach (var choice in new[] { mode, saves }) { choice.DisplayMemberPath = "Label"; choice.SelectedValuePath = "Value"; }

        var root = new Grid { Margin = new(28, 24, 28, 20) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new(0, 0, 0, 20) };
        counts.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(counts, Dock.Right); header.Children.Add(counts);
        var brand = new StackPanel(); brand.Children.Add(new TextBlock { Text = "SteamFusion", FontSize = 28, FontWeight = FontWeights.SemiBold });
        var subtitle = Ui.Text("一个游戏库，交给对应的账号启动。", 13, true); subtitle.Margin = new(0, 5, 0, 0); brand.Children.Add(subtitle);
        var identity = new StackPanel { Orientation = Orientation.Horizontal }; identity.Children.Add(Ui.Logo()); identity.Children.Add(brand);
        header.Children.Add(identity); root.Children.Add(header);
        var tabs = new TabControl(); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "游戏路由", Content = GamePage() });
        tabs.Items.Add(new TabItem { Header = "账号与环境", Content = AccountPage() });
        tabs.Items.Add(new TabItem { Header = "工具连接", Content = ToolsPage() });
        tabs.Items.Add(new TabItem { Header = "数据导出", Content = new ExportPage(config) });
        var footer = new Grid { Margin = new(0, 18, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status.MaxHeight = 78; status.VerticalAlignment = VerticalAlignment.Center; status.Margin = new(0, 0, 20, 0);
        var statusScroll = Ui.Scroll(status); statusScroll.MaxHeight = 78; footer.Children.Add(statusScroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        AddAction(actions, Ui.AsyncButton("检查运行状态", CheckStatus));
        AddAction(actions, Ui.AsyncButton("取消当前操作", async () => SetStatus((await controller.Handle(new("cancel"))).Message)));
        AddAction(actions, Ui.AsyncButton("退出后台", async () => SetStatus((await controller.Handle(new("exit"))).Message)), false);
        selectedGame.SelectionChanged += (_, _) => { if (!refreshing) SelectGame(); };
        search.TextChanged += (_, _) => { gamesView?.Refresh(); UpdateListCount(); };
        RefreshGames("368340");
        SetStatus("点击 × 后继续在托盘运行，双击右下角的 SteamFusion 图标可重新打开设置。");
    }

    private UIElement GamePage()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(330) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new());
        var library = new DockPanel();
        var top = new StackPanel { Margin = new(0, 0, 0, 14) }; DockPanel.SetDock(top, Dock.Top); library.Children.Add(top);
        top.Children.Add(Ui.Heading("游戏")); top.Children.Add(Ui.Field("搜索名称或 AppID", search)); top.Children.Add(listCount);
        var actions = new Grid { Margin = new(0, 14, 0, 0) }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
        var import = Ui.Button("导入游戏库", Import); import.Margin = new(0, 0, 8, 0); actions.Children.Add(import);
        var add = Ui.Button("添加 AppID", AddGame); Grid.SetColumn(add, 1); actions.Children.Add(add); DockPanel.SetDock(actions, Dock.Bottom); library.Children.Add(actions);
        var gameTemplate = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding("Name")); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); name.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium); gameTemplate.AppendChild(name);
        var meta = new FrameworkElementFactory(typeof(TextBlock)); meta.SetBinding(TextBlock.TextProperty, new Binding("Meta")); meta.SetValue(TextBlock.FontSizeProperty, 11d); meta.SetValue(TextBlock.ForegroundProperty, Ui.Brush("MutedBrush")); meta.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0)); gameTemplate.AppendChild(meta);
        selectedGame.ItemTemplate = new DataTemplate { VisualTree = gameTemplate }; library.Children.Add(selectedGame);
        grid.Children.Add(Ui.Card(library, 18));
        var detail = new DockPanel();
        var detailHeader = new StackPanel { Margin = new(0, 0, 0, 20) }; detailHeader.Children.Add(gameTitle); detailHeader.Children.Add(gameMeta); DockPanel.SetDock(detailHeader, Dock.Top); detail.Children.Add(detailHeader);
        editor.Children.Add(Ui.Field("游玩账号", account)); editor.Children.Add(Ui.Field("运行方式", mode)); editor.Children.Add(Ui.Field("存档方式", saves)); editor.Children.Add(enabled); editor.Children.Add(automatic);
        var hint = Ui.Text("自动路由会优先使用对应账号所在的客户端；需要另一个账号时使用沙盒。只有必须普通运行的游戏才需要切号。", 12, true); hint.Margin = new(0, 12, 0, 0); editor.Children.Add(hint);
        gameActions.Margin = new(0, 16, 0, 0); DockPanel.SetDock(gameActions, Dock.Bottom); detail.Children.Add(gameActions);
        AddAction(gameActions, Ui.Button("保存设置", Save, true)); AddAction(gameActions, Ui.AsyncButton("到所属账号下载", async () =>
        {
            if (!uint.TryParse(selectedGame.SelectedValue as string, out var id)) return;
            Save(); SetStatus((await controller.Handle(new("download", AppId: id, RequestId: Guid.NewGuid()))).Message);
        }));
        detail.Children.Add(Ui.Scroll(editor)); var card = Ui.Card(detail, 26); Grid.SetColumn(card, 2); grid.Children.Add(card); return grid;
    }
    private UIElement AccountPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Heading("账号与运行环境"));
        var note = Ui.Text("这里显示已配置的账号；实际登录情况可通过底部“检查运行状态”查看。", 13, true); note.Margin = new(0, 0, 0, 20); panel.Children.Add(note);
        var accounts = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new(0, 0, 0, 20) };
        foreach (var a in config.Accounts)
        {
            var info = new StackPanel(); info.Children.Add(Ui.Heading(a.Name)); info.Children.Add(Ui.Text("登录名  " + a.LoginName, 12, true));
            var box = Ui.Text("沙盒  " + a.SandboxName, 12, true); box.Margin = new(0, 8, 0, 16); info.Children.Add(box);
            info.Children.Add(Ui.AsyncButton("将此账号切到普通客户端", async () => SetStatus((await controller.Handle(new("swap", AccountId: a.Id, RequestId: Guid.NewGuid()))).Message)));
            var card = Ui.Card(info); card.Margin = new(0, 0, 12, 0); accounts.Children.Add(card);
        }
        panel.Children.Add(accounts);
        if (config.Accounts.Count == 0) panel.Children.Add(Ui.Text("请先在 Steam 中分别登录账号，再重新打开此窗口。"));
        var defaults = new StackPanel(); defaults.Children.Add(Ui.Heading("启动偏好"));
        defaults.Children.Add(Ui.Field("Steam 未运行时，默认普通账号", native)); defaults.Children.Add(dual);
        var save = Ui.Button("保存设置", Save, true); save.HorizontalAlignment = HorizontalAlignment.Left; save.Margin = new(0, 18, 0, 0); defaults.Children.Add(save); panel.Children.Add(Ui.Card(defaults));
        return Ui.Scroll(panel);
    }
    private UIElement ToolsPage()
    {
        var panel = new StackPanel(); var paths = new StackPanel(); paths.Children.Add(Ui.Heading("本机工具"));
        paths.Children.Add(Ui.Text("账号切换已内置，使用 Steam 已记住的账号。", 12, true));
        AddPath(paths, "Steam 客户端", steam); AddPath(paths, "Sandboxie · Start.exe", sandbox);
        var save = Ui.Button("保存设置", Save, true); save.HorizontalAlignment = HorizontalAlignment.Left; paths.Children.Add(save); panel.Children.Add(Ui.Card(paths));
        var advanced = new StackPanel(); advanced.Children.Add(Ui.Heading("游戏库与下载")); advanced.Children.Add(integrate); advanced.Children.Add(uninstalled);
        advanced.Children.Add(Ui.Text("已安装游戏保留 Steam 原生详情页。取消备用入口选项后，会移除自动生成的非 Steam 入口。", 12, true));
        var shared = Ui.Text(config.SharedLibraryDownloads ? "共享库下载已启用。同一游戏请只在一个客户端中更新或卸载。" : "共享库下载未启用，沙盒下载的文件可能保留在沙盒内。", 12, true); shared.Margin = new(0, 12, 0, 14); advanced.Children.Add(shared);
        var install = Ui.Button("添加到 Steam 游戏库", InstallShortcuts); install.HorizontalAlignment = HorizontalAlignment.Left; advanced.Children.Add(install);
        var card = Ui.Card(advanced); card.Margin = new(0, 18, 0, 0); panel.Children.Add(card);
        var path = Ui.Text("配置位置  " + Discovery.DataDirectory, 12, true); path.Margin = new(0, 16, 0, 8); panel.Children.Add(path); return Ui.Scroll(panel);
    }
    private void RefreshGames(string? selection)
    {
        refreshing = true;
        try
        {
            gameItems = scan.Games.Where(g => g.AppId != 228980).Select(g => new GameItem(g.Name, g.AppId.ToString(), true))
                .Concat(config.Games.Select(g => new GameItem(g.Name, g.AppId.ToString(), false)))
                .DistinctBy(g => g.Value).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            gamesView = CollectionViewSource.GetDefaultView(gameItems);
            gamesView.Filter = item => item is GameItem game && (game.Name + " " + game.Value).Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
            selectedGame.ItemsSource = gamesView; selectedGame.SelectedValue = selection;
            if (selectedGame.SelectedIndex < 0 && !gamesView.IsEmpty) selectedGame.SelectedIndex = 0;
        }
        finally { refreshing = false; }
        SelectGame(); UpdateListCount(); UpdateCounts();
        if (selectedGame.SelectedItem is not null) selectedGame.ScrollIntoView(selectedGame.SelectedItem);
    }
    private void UpdateCounts() => counts.Text = $"{config.Accounts.Count} 个账号   /   {config.Games.Count(g => g.Enabled)} 条已启用规则";
    private void UpdateListCount() => listCount.Text = gamesView is null ? "" : gamesView.IsEmpty ? "没有匹配的游戏，试试其他名称或 AppID。" : $"{gamesView.Cast<GameItem>().Count()} 个游戏";
    private void Import()
    {
        new LibraryImportWindow(controller) { Owner = this }.ShowDialog();
        if (File.Exists(Discovery.Store.ConfigPath)) config = config with { Games = Discovery.Store.Load().Games };
        RefreshGames(selectedGame.SelectedValue as string);
        SetStatus($"已启用 {config.Games.Count(g => g.Enabled)} 条规则。新游戏可到所属账号下载。");
    }
    private void AddGame()
    {
        var dialog = new AddGameWindow { Owner = this }; if (dialog.ShowDialog() != true) return;
        if (!scan.Games.Any(g => g.AppId == dialog.AppId) && !config.Games.Any(g => g.AppId == dialog.AppId))
            config.Games.Add(new Game(dialog.AppId, dialog.GameName, config.Accounts.FirstOrDefault()?.Id ?? "") { Enabled = false });
        search.Clear(); RefreshGames(dialog.AppId.ToString());
    }
    public void SetStatus(string text) => status.Text = text;
    private async Task CheckStatus()
    {
        var snapshot = await controller.Probe(config);
        var lines = snapshot.Instances.Select(i =>
        {
            var name = config.Accounts.FirstOrDefault(a => a.SteamId == i.SteamId)?.Name ?? "账号待确认";
            return $"{(i.IsNative ? "普通客户端" : i.Environment)} · {name} · {(i.Verified ? "身份已核实" : "身份待确认")}";
        }).ToList();
        if (lines.Count == 0) lines.Add("未检测到已管理的 Steam 客户端。");
        if (snapshot.HasUnmanagedInstances) lines.Add("另有未受管理的 Steam 实例，请先检查。");
        SetStatus(string.Join("\n", lines));
    }
    private void SelectGame()
    {
        var item = selectedGame.SelectedItem as GameItem; editor.IsEnabled = gameActions.IsEnabled = item is not null;
        gameTitle.Text = item?.Name ?? "选择一个游戏"; gameMeta.Text = item is null ? "从左侧列表选择，或搜索游戏名称。" : $"APP {item.Value}   ·   {(item.Installed ? "本机已安装" : "未在本机库检测到安装")}";
        if (item is null || !uint.TryParse(item.Value, out var id)) return;
        var g = config.Games.FirstOrDefault(g => g.AppId == id);
        account.SelectedValue = g?.AccountId ?? config.Accounts.FirstOrDefault()?.Id;
        enabled.IsChecked = g?.Enabled ?? false; automatic.IsChecked = g?.AutoSwap ?? true;
        mode.SelectedValue = g?.Mode == RunMode.NativeOnly ? "native" : "auto";
        saves.SelectedValue = g?.Saves switch { SaveMode.SteamCloud => "cloud", SaveMode.FixedEnvironment => g.FixedEnvironment == "native" ? "native" : "sandbox", _ => "unknown" };
    }
    private void Save()
    {
        if (controller.Busy) throw new InvalidOperationException("请等待当前操作结束再修改配置。");
        var updated = config with { SteamExe = steam.Text.Trim(), SandboxieStartExe = sandbox.Text.Trim(),
            DefaultNativeAccountId = native.SelectedValue as string ?? "", KeepBothOnline = dual.IsChecked == true,
            IntegrateLibrary = integrate.IsChecked == true, UninstalledLibrary = uninstalled.IsChecked == true,
            SharedLibraryDownloads = File.Exists(Discovery.Store.ConfigPath) ? Discovery.Store.Load().SharedLibraryDownloads : config.SharedLibraryDownloads,
            Games = [.. (File.Exists(Discovery.Store.ConfigPath) ? Discovery.Store.Load().Games : config.Games)] };
        if (uint.TryParse(selectedGame.SelectedValue as string, out var id) && account.SelectedValue is string accountId)
        {
            var source = scan.Games.FirstOrDefault(g => g.AppId == id);
            var selected = updated.Accounts.Single(a => a.Id == accountId);
            var save = saves.SelectedValue as string;
            var game = new Game(id, source?.Name ?? config.Games.FirstOrDefault(g => g.AppId == id)?.Name ?? id.ToString(), accountId)
            {
                Enabled = enabled.IsChecked == true, AutoSwap = automatic.IsChecked == true,
                Mode = mode.SelectedValue as string == "native" ? RunMode.NativeOnly : RunMode.Auto,
                Saves = save switch { "cloud" => SaveMode.SteamCloud, "native" or "sandbox" => SaveMode.FixedEnvironment, _ => SaveMode.Unknown },
                FixedEnvironment = save == "native" ? "native" : save == "sandbox" ? selected.SandboxName : null,
                PauseOtherSandbox = config.Games.FirstOrDefault(g => g.AppId == id)?.PauseOtherSandbox ?? false
            };
            updated.Games.RemoveAll(g => g.AppId == id); updated.Games.Add(game);
        }
        Discovery.Store.Save(updated); Discovery.WritePluginConfiguration(updated); config = updated; UpdateCounts();
        SetStatus("设置已保存。已启用的游戏可以通过 Steam 库入口路由。");
    }
    private void InstallShortcuts()
    {
        Save();
        if (!config.Games.Any(g => g.Enabled)) throw new InvalidOperationException("请先确认并启用至少一个游戏。");
        SetStatus(LibraryShortcuts.Install(config, Path.Combine(AppContext.BaseDirectory, "SteamFusion.Cli.exe")));
    }
    private static void AddPath(Panel parent, string title, TextBox input, string? help = null)
    {
        var dock = new DockPanel(); var browse = Ui.Button("浏览…", () =>
        { var dialog = new OpenFileDialog { Filter = "程序 (*.exe)|*.exe" }; if (dialog.ShowDialog() == true) input.Text = dialog.FileName; });
        browse.Margin = new(10, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); dock.Children.Add(browse); dock.Children.Add(input); parent.Children.Add(Ui.Field(title, dock, help));
        System.Windows.Automation.AutomationProperties.SetName(input, title);
    }
    private static void AddAction(Panel panel, Button button, bool margin = true)
    { if (margin) button.Margin = new(0, 0, 8, 0); panel.Children.Add(button); }
    private sealed record Item(string Label, string Value);
    private sealed record GameItem(string Name, string Value, bool Installed)
    { public string Meta => $"{Value}  ·  {(Installed ? "已安装" : "游戏库规则")}"; }
}

public sealed class AddGameWindow : Window
{
    public uint AppId { get; private set; }
    public string GameName { get; private set; } = "";
    public AddGameWindow()
    {
        Ui.Apply(this); Title = "SteamFusion · 添加游戏"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(28) }; Content = panel;
        panel.Children.Add(Ui.Heading("添加游戏规则"));
        var description = Ui.Text("使用 Steam 商店链接中的 AppID 添加游戏。", 12, true); description.Margin = new(0, 0, 0, 22); panel.Children.Add(description);
        var id = new TextBox(); var name = new TextBox(); panel.Children.Add(Ui.Field("Steam AppID", id)); panel.Children.Add(Ui.Field("游戏名称", name));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 6, 0, 0) };
        var cancel = Ui.Button("取消", Close); cancel.IsCancel = true; cancel.Margin = new(0, 0, 8, 0); actions.Children.Add(cancel);
        var add = Ui.Button("添加游戏", () =>
        {
            if (!uint.TryParse(id.Text.Trim(), out var appId) || appId == 0 || string.IsNullOrWhiteSpace(name.Text))
            { MessageBox.Show("请输入有效 AppID 和游戏名称。", "SteamFusion"); return; }
            AppId = appId; GameName = name.Text.Trim(); DialogResult = true;
        }, true); add.IsDefault = true; actions.Children.Add(add); panel.Children.Add(actions);
        Loaded += (_, _) => id.Focus();
    }
}

public sealed class ConfirmationWindow : Window
{
    public ConfirmationWindow(string title, string text)
    {
        Ui.Apply(this); Title = title; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
        var panel = new StackPanel { Margin = new(28) }; Content = panel;
        panel.Children.Add(Ui.Heading(title)); panel.Children.Add(Ui.Text(text, 14));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0) };
        var no = Ui.Button("取消", Close); no.IsCancel = true; no.Margin = new(0, 0, 10, 0);
        var yes = Ui.Button("是，已确认", () => DialogResult = true, true);
        buttons.Children.Add(no); buttons.Children.Add(yes); panel.Children.Add(buttons);
    }
}
