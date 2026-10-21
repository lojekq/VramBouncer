using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using VramBouncer.Shell;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace VramBouncer.Features;

/// <summary>What the Overview buttons and the tray menu do. Under --demo there is no engine and they do nothing.</summary>
public static class Actions
{
    public static void EvictNow() => Engine.I?.SetManual(true); // until "Restore now" or the switch: a grace would bring them back in 60 s
    public static void RestoreNow() => Engine.I?.RestoreNow();
    public static void SetManual(bool on) => Engine.I?.SetManual(on);
    public static void Pause(TimeSpan? d) => Engine.I?.Command(Cmd.Pause, d is { } x ? DateTime.UtcNow + x : null);
    public static void Resume() => Engine.I?.Command(Cmd.Resume);

    public static ContextMenu PauseMenu()
    {
        var m = new ContextMenu();
        foreach (var i in PauseItems()) m.Items.Add(i);
        return m;
    }

    public static List<Control> PauseItems() =>
    [
        Tray.Item(Loc.T("Pause.15m"), () => Pause(TimeSpan.FromMinutes(15))),
        Tray.Item(Loc.T("Pause.1h"), () => Pause(TimeSpan.FromHours(1))),
        Tray.Item(Loc.T("Pause.Restart"), () => Pause(null)),
        new Separator(),
        Tray.Item(Loc.T("Pause.Off"), Resume),
    ];

    /// <summary>Exit while models are out: bring them back now, or at the next start (state.json stays).</summary>
    public static bool ConfirmExit()
    {
        if (Engine.I is not { HasEvicted: true } e) return true;
        var r = System.Windows.MessageBox.Show(Loc.T("Exit.Message"), AppInfo.DisplayName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) e.RestoreNow(() => App.Quit(confirm: false));
        return r == MessageBoxResult.No;
    }
}

/// <summary>Three tray icons (normal / game mode / pause), the tooltip, a status line and the app's menu items.</summary>
public static class TrayState
{
    static TaskbarIcon? icon;
    static readonly MenuItem Status = new() { IsEnabled = false };
    static readonly MenuItem ManualItem = new() { Header = Loc.T("Tray.Manual"), IsCheckable = true };
    static readonly Dictionary<string, BitmapImage> Icons = [];
    static string? shown;

    public static IEnumerable<Control> Items()
    {
        ManualItem.Click += (_, _) => Actions.SetManual(ManualItem.IsChecked);
        var pause = new MenuItem { Header = Loc.T("Tray.Pause") };
        foreach (var i in Actions.PauseItems()) pause.Items.Add(i);
        return [ManualItem, pause, Tray.Item(Loc.T("Tray.Evict"), Actions.EvictNow), Tray.Item(Loc.T("Tray.Restore"), Actions.RestoreNow)];
    }

    /// <summary>
    /// After Tray.Create. The template keeps its TaskbarIcon private and has no API for state icons, so it is taken by reflection
    /// (the template is frozen, 00-shared §9.2). Its own icon refresh runs only when the taskbar theme flips; we re-apply after it.
    /// </summary>
    public static void Attach()
    {
        icon = typeof(Tray).GetField("icon", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as TaskbarIcon;
        if (icon is null) { Log.Warn("tray icon not found: state icons off"); return; }
        icon.ContextMenu?.Items.Insert(0, Status);
        icon.ContextMenu?.Items.Insert(1, new Separator());
        Vm.I.Changed += Update;
        MessageWindow.WndProc += (nint _, int msg, nint _, nint _, ref bool _) =>
        {
            if (msg == MessageWindow.WM_SETTINGCHANGE) { shown = null; Update(); }
            return 0;
        };
        Update();
    }

    static void Update()
    {
        if (icon is null) return;
        var vm = Vm.I;
        var name = vm.IsPaused ? "tray-paused" : vm.InGameMode ? "tray-game" : "tray-normal";
        if (name != shown)
        {
            if (!Icons.TryGetValue(name, out var img))
                Icons[name] = img = new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}.ico"));
            icon.IconSource = img;
            shown = name;
        }
        var tip = vm.Tooltip;
        icon.ToolTipText = tip.Length > 120 ? tip[..120] : tip; // the shell cuts tooltips at 128 chars
        Status.Header = vm.Header;
        ManualItem.IsChecked = vm.Manual;
    }
}

/// <summary>The app's section on the Settings page.</summary>
public static class AppSettings
{
    public static FrameworkElement Create()
    {
        var s = Settings.Current;
        var t = s.Timings;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Bouncer"), FontTypography = FontTypography.Subtitle, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(Number("Settings.Grace", "Settings.Grace.Note", SymbolRegular.Timer24, t.GraceSec, v => t.GraceSec = (int)Math.Clamp(v, 5, 3600)));
        panel.Children.Add(Number("Settings.Margin", "Settings.Margin.Note", SymbolRegular.DataHistogram24, t.MarginGb, v => t.MarginGb = Math.Clamp(v, 0, 8)));
        panel.Children.Add(Number("Settings.WaitVram", "Settings.WaitVram.Note", SymbolRegular.HourglassHalf24, t.WaitVramMin, v => t.WaitVramMin = (int)Math.Clamp(v, 1, 120)));
        panel.Children.Add(Number("Settings.Guard", "Settings.Guard.Note", SymbolRegular.ShieldCheckmark24, t.GuardSec, v => t.GuardSec = (int)Math.Clamp(v, 5, 600)));

        var n = s.Notify;
        var checks = new WrapPanel();
        foreach (var (key, get, set) in new (string, Func<bool>, Action<bool>)[]
        {
            ("Settings.Notify.Evict", () => n.Evict, v => n.Evict = v),
            ("Settings.Notify.Restore", () => n.Restore, v => n.Restore = v),
            ("Settings.Notify.Errors", () => n.Errors, v => n.Errors = v),
            ("Settings.Notify.Summary", () => n.Summary, v => n.Summary = v),
        })
        {
            var c = new CheckBox { Content = Loc.T(key), IsChecked = get(), Margin = new(0, 0, 16, 0) };
            c.Click += (_, _) => { set(c.IsChecked == true); Settings.Save(); };
            checks.Children.Add(c);
        }
        panel.Children.Add(Card("Settings.Notify", null, SymbolRegular.Alert24, checks));

        var kill = new ToggleSwitch { IsChecked = s.KillWithoutRestore };
        kill.Click += (_, _) => { s.KillWithoutRestore = kill.IsChecked == true; Settings.Save(); };
        panel.Children.Add(Card("Settings.KillAnyway", "Settings.KillAnyway.Note", SymbolRegular.Warning24, kill));
        return panel;
    }

    static CardControl Number(string key, string note, SymbolRegular icon, double value, Action<double> set)
    {
        var box = new TextBox { Text = value.ToString(), Width = 90, TextAlignment = TextAlignment.Right };
        box.LostFocus += (_, _) =>
        {
            if (double.TryParse(box.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) set(v);
            Settings.Save();
            box.Text = Read(key).ToString();
        };
        return Card(key, note, icon, box);
    }

    static double Read(string key) => key switch
    {
        "Settings.Grace" => Settings.Current.Timings.GraceSec,
        "Settings.Margin" => Settings.Current.Timings.MarginGb,
        "Settings.WaitVram" => Settings.Current.Timings.WaitVramMin,
        _ => Settings.Current.Timings.GuardSec,
    };

    static CardControl Card(string key, string? note, SymbolRegular icon, object content)
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = Loc.T(key) });
        if (note is not null)
            header.Children.Add(new TextBlock { Text = Loc.T(note), FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary, TextWrapping = TextWrapping.Wrap });
        return new CardControl { Header = header, Icon = new SymbolIcon(icon) { FontSize = 24 }, Content = content, Margin = new(0, 0, 0, 4) };
    }
}
