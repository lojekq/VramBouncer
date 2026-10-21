using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public sealed class CommandRow(UserCommand c) : INotifyPropertyChanged
{
    public UserCommand Cmd => c;
    string check = "";
    public string CheckText { get => check; set { check = value; PropertyChanged?.Invoke(this, new(nameof(CheckText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Backend cards bound straight to the settings objects; any edit is saved when it leaves the field.</summary>
public partial class ModelsPage
{
    public static LlamaConfig Llama => Settings.Current.Backends.Llama;
    public static OllamaConfig Ollama => Settings.Current.Backends.Ollama;
    public static ComfyConfig Comfy => Settings.Current.Backends.Comfy;
    public static LmStudioConfig LmStudio => Settings.Current.Backends.LmStudio;

    /// <summary>The last "Check eviction" result: the demo fills it for the screenshot.</summary>
    public static List<string> LastPlan { get; set; } = [];

    readonly ObservableCollection<CommandRow> rows = [];

    public ModelsPage()
    {
        InitializeComponent();
        foreach (var c in Settings.Current.Backends.Commands) rows.Add(new(c));
        CommandList.ItemsSource = rows;
        PlanList.ItemsSource = LastPlan;
        OllamaUrl.PlaceholderText = Http.OllamaDefault(Environment.GetEnvironmentVariable("OLLAMA_HOST"));

        AddHandler(LostFocusEvent, new RoutedEventHandler((_, _) => Save()));
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Save()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Save()));

        AddCommand.Click += (_, _) =>
        {
            var c = new UserCommand { Name = Loc.T("Command.NewName") };
            Settings.Current.Backends.Commands.Add(c);
            rows.Add(new(c));
        };
        HandsOffButton.Click += (_, _) => { Llama.HandsOff = false; Draw(); };
        LmTokenNote.Text = LmStudio.Token is null ? "" : Loc.T("Models.TokenSaved");
        LmToken.LostFocus += (_, _) =>
        {
            if (LmToken.Password is not { Length: > 0 } t) return;
            LmStudio.Token = LmStudioConfig.Protect(t); // DPAPI, this Windows user only
            LmToken.Password = "";
            LmTokenNote.Text = Loc.T("Models.TokenSaved");
            Save();
        };
        PlanButton.Click += async (_, _) =>
        {
            if (Engine.I is not { } e) return;
            PlanButton.IsEnabled = false;
            try { LastPlan = await e.Plan(); }
            catch (Exception ex) { LastPlan = [ex.Message]; }
            PlanList.ItemsSource = LastPlan;
            PlanButton.IsEnabled = true;
        };
        Loaded += (_, _) => { Vm.I.Changed += Draw; Draw(); };
        Unloaded += (_, _) => Vm.I.Changed -= Draw;
    }

    static void Save()
    {
        Settings.Save();
        Engine.I?.RefreshCatalog(); // re-probe with the new addresses at once
    }

    void Draw()
    {
        var p = Vm.I.View.Probes;
        LlamaStatus.Text = Status("llama-server", Llama.On, Llama.Port);
        OllamaStatus.Text = Status("ollama", Ollama.On, null);
        ComfyStatus.Text = Status("comfyui", Comfy.On, null);
        LmStatus.Text = Status("lmstudio", LmStudio.On, null);
        var lp = p.GetValueOrDefault("llama-server");
        LlamaCaptured.Text = lp?.Detail ?? "";
        LlamaWarnings.ItemsSource = lp?.Warnings ?? (Llama.HandsOff ? [Loc.T("Llama.HandsOff")] : []);
        HandsOffButton.Visibility = Llama.HandsOff ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Status(string kind, bool on, int? port)
    {
        if (!on) return Loc.T("Models.Off");
        if (Vm.I.View.Probes.GetValueOrDefault(kind) is not { } p) return "…";
        if (!p.Found) return p.Warnings.FirstOrDefault() ?? (port is { } n ? string.Format(Loc.T("Models.NotRunningPort"), n) : Loc.T("Models.NotFound"));
        var parts = new List<string> { Loc.T("Models.Found") + (p.Version is { } v && kind != "llama-server" ? " · v" + v : "") };
        if (port is { } pt) parts.Add(":" + pt);
        parts.Add(p.Loaded.Count == 0 ? Loc.T("Models.NothingLoaded")
            : string.Join(", ", p.Loaded.Select(s => s.VramBytes > 0 ? $"{s.Label} ({Engine.Gb(s.VramBytes)} {Loc.T("Unit.GB")})" : s.Label)));
        if (kind == "llama-server" && p.Version == "loading") parts.Add(Loc.T("Models.Loading"));
        if (kind == "llama-server" && p.Version == "router") parts.Insert(1, Loc.T("Models.Router"));
        if (kind != "llama-server") parts.AddRange(p.Warnings); // llama-server has its own list below
        return string.Join(" · ", parts);
    }

    async void RunCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandRow row || Engine.I is not { } engine) return;
        if (row.Cmd.Check.Length == 0) { row.CheckText = Loc.T("Command.NoCheck"); return; }
        row.CheckText = "…";
        try
        {
            row.CheckText = await engine.Check(row.Cmd) switch
            {
                true => Loc.T("Command.Loaded"), false => Loc.T("Command.NotLoaded"), null => Loc.T("Command.CheckFailed"),
            };
        }
        catch (Exception ex) { row.CheckText = ex.Message; }
    }

    void Remove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandRow row) return;
        Settings.Current.Backends.Commands.Remove(row.Cmd);
        rows.Remove(row);
        Settings.Save();
    }
}
