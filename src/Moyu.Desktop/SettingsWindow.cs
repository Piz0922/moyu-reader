using System.Windows.Interop;

namespace Moyu.Desktop;

public sealed class SettingsWindow : Window
{
    private readonly App app;
    private readonly Slider size, line, opacity;
    private readonly ComboBox font;
    private readonly TextBox background, foreground, hotkey;
    private readonly TextBlock error;
    private uint modifiers, key;
    public SettingsWindow(App app)
    {
        this.app = app;
        Style = (Style)app.FindResource(typeof(Window));
        Title = "摸鱼阅读器 · 设置"; Width = 510; Height = 740; MinWidth = 440; MinHeight = 450;
        Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var p = app.Store.Preferences;
        modifiers = p.HotkeyModifiers; key = p.HotkeyKey;
        var root = new DockPanel { Margin = new Thickness(28, 22, 28, 20) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close();
        var apply = new Button { Content = "保存设置", Style = (Style)app.FindResource("Primary") };
        apply.Click += Apply;
        footer.Children.Add(cancel); footer.Children.Add(apply); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var stack = new StackPanel(); root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        stack.Children.Add(new TextBlock { Text = "让它适合你的阅读", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "正文里不显示提示和工具栏。", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 18) });
        void Label(string label) => stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 6), FontWeight = FontWeights.Medium });
        Label("字体");
        font = new ComboBox { IsEditable = false, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToArray(), SelectedItem = p.FontFamily };
        stack.Children.Add(font);
        Slider AddSlider(string label, double min, double max, double value, string format)
        {
            Label(label);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 14, 4) };
            var number = new TextBlock { Text = value.ToString(format), HorizontalAlignment = HorizontalAlignment.Right };
            slider.ValueChanged += (_, _) => number.Text = slider.Value.ToString(format);
            Grid.SetColumn(number, 1); grid.Children.Add(slider); grid.Children.Add(number); stack.Children.Add(grid); return slider;
        }
        size = AddSlider("字号", 8, 72, p.FontSize, "0.0");
        line = AddSlider("行距", 1.1, 2.5, p.LineSpacing, "0.00");
        opacity = AddSlider("背景不透明度（不影响文字）", 0, 1, p.BackgroundOpacity, "P0");
        Label("背景颜色 / 文字颜色");
        var colors = new Grid(); colors.ColumnDefinitions.Add(new ColumnDefinition()); colors.ColumnDefinitions.Add(new ColumnDefinition());
        background = new TextBox { Text = p.Background, Margin = new Thickness(0, 0, 8, 0) };
        foreground = new TextBox { Text = p.Foreground, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(background, "背景颜色");
        System.Windows.Automation.AutomationProperties.SetName(foreground, "文字颜色");
        Grid.SetColumn(foreground, 1); colors.Children.Add(background); colors.Children.Add(foreground); stack.Children.Add(colors);
        Label("隐藏 / 显示快捷键（点击后按组合键）");
        hotkey = new TextBox { Text = KeyLabel(modifiers, key), IsReadOnly = true };
        error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(165, 58, 45)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6), Text = app.HotkeyWarning };
        hotkey.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 || mods.HasFlag(ModifierKeys.Windows))
            { error.Text = "请使用包含 Ctrl 或 Alt 的组合键。"; return; }
            modifiers = (uint)mods; key = (uint)KeyInterop.VirtualKeyFromKey(pressed); hotkey.Text = KeyLabel(modifiers, key); error.Text = "";
        };
        stack.Children.Add(hotkey);
        stack.Children.Add(error);
        var recover = new Button { Content = "找回阅读窗口", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        recover.Click += (_, _) => { Close(); app.RecoverReader(); };
        stack.Children.Add(recover);
        stack.Children.Add(new TextBlock { Text = "书籍与进度保存在程序旁的 resources 目录。\n备份时复制整个程序文件夹即可。", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap });
    }

    public static string KeyLabel(uint modifiers, uint key)
    {
        var names = new List<string>();
        if ((modifiers & 2) != 0) names.Add("Ctrl"); if ((modifiers & 1) != 0) names.Add("Alt"); if ((modifiers & 4) != 0) names.Add("Shift");
        names.Add(key == 32 ? "空格" : KeyInterop.KeyFromVirtualKey((int)key).ToString()); return string.Join(" + ", names);
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        try
        {
            var bg = (Color)ColorConverter.ConvertFromString(background.Text.Trim());
            var fg = (Color)ColorConverter.ConvertFromString(foreground.Text.Trim());
            if (!app.ChangeHotkey(modifiers, key)) { error.Text = "这个快捷键已被占用，请换一个组合。原快捷键仍然保留。"; return; }
            var p = app.Store.Preferences;
            p.Background = $"#{bg.R:X2}{bg.G:X2}{bg.B:X2}"; p.Foreground = $"#{fg.R:X2}{fg.G:X2}{fg.B:X2}";
            p.FontSize = size.Value; p.LineSpacing = line.Value; p.BackgroundOpacity = opacity.Value;
            p.FontFamily = font.SelectedItem as string ?? "Microsoft YaHei UI";
            app.Reader.ApplyAppearance(); app.SaveNow(); Close();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        { error.Text = "颜色请填写 #FFFFFF 这样的六位十六进制值。"; }
        catch (Exception ex) { error.Text = "保存失败：" + ex.Message; }
    }
}
