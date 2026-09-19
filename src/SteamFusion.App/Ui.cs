using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SteamFusion.App;

internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static void Apply(Window window)
    {
        window.Background = Brush("CanvasBrush"); window.Foreground = Brush("TextBrush");
        window.FontFamily = new FontFamily("Microsoft YaHei UI"); window.FontSize = 13;
        window.UseLayoutRounding = true;
        window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/SteamFusion;component/Assets/SteamFusion.ico"));
        window.SourceInitialized += (_, _) =>
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
        };
    }
    public static Image Logo() => new()
    {
        Width = 54, Height = 54, Margin = new(0, 0, 14, 0),
        Source = new BitmapImage(new Uri("pack://application:,,,/SteamFusion;component/Assets/SteamFusion.png")),
        VerticalAlignment = VerticalAlignment.Center
    };
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
    public static TextBlock Text(string text, double size = 13, bool muted = false) => new()
    { Text = text, FontSize = size, Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap };
    public static TextBlock Heading(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextBrush"), FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10) };
    public static Border Card(UIElement child, double padding = 22) => new()
    { Child = child, Padding = new(padding), Background = Brush("SurfaceBrush"), CornerRadius = new(10), BorderBrush = Brush("LineBrush"), BorderThickness = new(1) };
    public static FrameworkElement Field(string title, FrameworkElement input, string? help = null)
    {
        AutomationProperties.SetName(input, title);
        var panel = new StackPanel { Margin = new(0, 0, 0, 16) };
        var label = Text(title, 12, true); label.Margin = new(0, 0, 0, 7);
        panel.Children.Add(label); panel.Children.Add(input);
        if (help is not null) { var hint = Text(help, 12, true); hint.Margin = new(0, 7, 0, 0); panel.Children.Add(hint); }
        return panel;
    }
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "SteamFusion"); } };
        return button;
    }
    public static Button AsyncButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "SteamFusion"); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
}
