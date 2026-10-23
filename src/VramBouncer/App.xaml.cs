using System.Diagnostics;
using System.Windows;
using VramBouncer.Demo;
using VramBouncer.Features;
using VramBouncer.Shell;

namespace VramBouncer;

public partial class App : Application
{
    /// <summary>Synchronous hook before exit: tray Exit, Windows session end, crash handler (best effort, before the error box).</summary>
    public static event Action? Exiting;

    /// <summary>Asked on a user exit (tray, restart); return false to stay. May ask the user.</summary>
    public static Func<bool>? ConfirmExit;

    /// <summary>"Remove data &amp; autostart" and uninstall: undo what the app registered and return null, or return why not (shown to the user).
    /// Set it before Installer.HandleAsync: --uninstall calls it there.</summary>
    public static Func<string?>? RemoveData;

    /// <summary>Installer turns autostart on for a fresh install. False for an app that should not start with Windows by default.</summary>
    public static bool AutostartOnInstall = true;

    static MainWindow? window;
    static bool quitting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var a = Args.Current;
        Crash.Install(this); // Settings and Loc are already loaded in Program.Main
        // Before the installer: --uninstall asks it. Models still out (state.json) would never come back once the program is gone
        RemoveData = () => (Engine.I?.HasEvicted ?? Store.Load() is { Evicted.Count: > 0 }) ? Loc.T("Remove.Evicted") : null;
        if (await Installer.HandleAsync()) // install, update, switch to the installed copy or uninstall: this process is done
        {
            Shutdown();
            return;
        }
        if (!SingleInstance.Acquire(() => Dispatcher.BeginInvoke(() => ShowMain(null)), () => Dispatcher.BeginInvoke(() => Quit(confirm: false))))
        {
            Shutdown();
            return;
        }
        Log.Info($"start {AppInfo.Version}{(a.Unknown.Count > 0 ? $", {a.Unknown.Count} unknown argument(s)" : "")}");

        if (a.Demo) DemoData.Fill();
        else if (Install.IsInstalled) Installer.InstalledStart(AutostartOnInstall); // Debug builds and exes elsewhere write nothing by themselves

        // App features: services, hotkeys, tray items, settings section
        SettingsPage.AppSection = AppSettings.Create;
        ConfirmExit = Actions.ConfirmExit;

        window = new MainWindow();
        if (a.Width > 0) (window.Width, window.Height) = (a.Width, a.Height);
        Theme.Apply(window, Settings.Current.Theme);
        window.Closing += (_, ce) =>
        {
            if (quitting) return;
            ce.Cancel = true; // the cross hides to the tray
            window.Hide();
            if (Settings.Current.TrayHintShown) return;
            Settings.Current.TrayHintShown = true;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, Loc.T("Tray.Hidden"));
        };
        window.IsVisibleChanged += (_, _) => Vm.I.WindowVisible = window.IsVisible;
        Capture.Attach(window, () => { if (a.Autoplay) Autoplay.Start(window); });

        if (a.Screenshot is null)
        {
            Tray.Create(ShowMain, () => Quit(), TrayState.Items());
            TrayState.Attach();
        }
        if (!a.Demo) Engine.Start(); // after the tray: the start-up check may already notify
        SessionEnding += (_, _) => RaiseExiting();
        Updates.Found += (version, url) =>
        {
            window.ShowUpdate(version, url);
            if (Settings.Current.NotifiedVersion == version) return;
            Settings.Current.NotifiedVersion = version;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Update.Message"), version), "about");
        };
        Updates.Start();

        if (!a.Background) ShowMain(a.Page);
    }

    public static void ShowMain(string? page)
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (page is not null) window.Navigate(page);
    }

    public static void RaiseExiting()
    {
        var hook = Exiting;
        Exiting = null; // once, whoever comes first
        foreach (var h in hook?.GetInvocationList() ?? [])
            try { ((Action)h)(); }
            catch (Exception e) { Log.Error("Exiting hook failed", e); } // the other hooks still run
    }

    public static void Quit(bool confirm = true)
    {
        if (confirm && ConfirmExit?.Invoke() == false) return;
        quitting = true;
        RaiseExiting();
        Tray.Dispose();
        Log.Info("exit");
        Current.Shutdown();
    }

    /// <summary>After a language change. The mutex is released first, so the new process does not just wake this one.</summary>
    public static void Restart()
    {
        if (ConfirmExit?.Invoke() == false) return;
        SingleInstance.Release();
        Process.Start(AppInfo.ExePath);
        Quit(confirm: false);
    }
}
