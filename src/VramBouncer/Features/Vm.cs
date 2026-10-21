using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public sealed record Seg(string Label, long Bytes, Brush Fill);

public enum Status { Loaded, Out, Busy, Error, Skip }

public sealed record ModelRow(string Backend, string Name, string Vram, string StatusText, Status Status)
{
    public Brush Dot => Status switch
    {
        Status.Loaded => Vm.Green, Status.Out => Vm.Orange, Status.Busy => Vm.Blue, Status.Error => Vm.Red, _ => Vm.Gray,
    };
}

/// <summary>A game in the Games table; the switch writes settings at once.</summary>
public sealed class GameRow(Game g, bool excluded, bool byDefault)
{
    public Game Game => g;
    public string Name => g.Name;
    public string Source => Loc.T("Source." + g.Source);
    public string Root => g.Root;
    public bool IsExcluded => excluded;
    public string Reason => byDefault ? Loc.T("Games.ByDefault") : Loc.T("Games.ByYou");

    public bool Evict
    {
        get => !Settings.Current.Games.Off.Contains(g.Root, StringComparer.OrdinalIgnoreCase);
        set
        {
            var off = Settings.Current.Games.Off;
            off.RemoveAll(r => string.Equals(r, g.Root, StringComparison.OrdinalIgnoreCase));
            if (!value) off.Add(g.Root);
            Settings.Save();
            Engine.I?.RefreshCatalog();
        }
    }
}

public sealed record SessionRow(Session S)
{
    public string When => S.Start.ToLocalTime().ToString("g");
    public string Game => S.Game;
    public string Duration => S.End is { } e ? Minutes(e - S.Start) : "—";
    static string Minutes(TimeSpan t) => t.TotalHours >= 1 ? string.Format(Loc.T("Unit.HourMin"), (int)t.TotalHours, t.Minutes) : string.Format(Loc.T("Unit.Min"), Math.Max(1, (int)t.TotalMinutes));
    public string Models => S.Evicted.Count == 0 ? Loc.T("Journal.Nothing")
        : $"{string.Join(", ", S.Evicted.Select(m => m.Name))} · {Engine.Gb(S.Evicted.Sum(m => m.Bytes))} {Loc.T("Unit.GB")}";
    public string Guard => S.Guard.Count == 0 ? "" : string.Join("\n", S.Guard);
    public bool HasGuard => S.Guard.Count > 0;
    public string Result => S.Result switch
    {
        "ok" => string.Format(Loc.T("Journal.Back"), S.RestoreSec),
        "warning" => string.Format(Loc.T("Journal.BackWarn"), S.RestoreSec, S.Note),
        "error" => string.Format(Loc.T("Journal.Error"), S.Note),
        _ => Loc.T("Journal.Pending"),
    };
    public Brush Dot => S.Result switch { "ok" => Vm.Green, "warning" => Vm.Orange, "error" => Vm.Red, _ => Vm.Gray };
}

/// <summary>
/// The window's and the tray's state, filled from <see cref="View"/> pictures (engine or demo).
/// One PropertyChanged("") refreshes every binding: the picture changes as a whole.
/// </summary>
public sealed class Vm : INotifyPropertyChanged
{
    public static Vm I { get; } = new();

    public static readonly Brush Green = Freeze("#2EA043"), Orange = Freeze("#F0883E"), Blue = Freeze("#2F81F7"),
        Red = Freeze("#F85149"), Gray = Freeze("#8B949E"), Purple = Freeze("#A371F7"), Teal = Freeze("#1F9D8B"), Pink = Freeze("#DB61A2");

    public volatile bool WindowVisible;
    /// <summary>The demo's fake clock for --autoplay; real time otherwise.</summary>
    public Func<DateTime> Now = () => DateTime.UtcNow;

    public View View { get; private set; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public ObservableCollection<GameRow> Games { get; } = [];
    public ObservableCollection<GameRow> Excluded { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public List<ModelRow> Models { get; private set; } = [];
    public List<Seg> Bar { get; private set; } = [];
    GameCatalog.Result? shownCatalog;
    string filter = "";

    bool applied;

    public void Apply(View v)
    {
        applied = true;
        View = v;
        Models = BuildModels(v);
        Bar = BuildBar(v);
        if (!ReferenceEquals(v.Catalog, shownCatalog)) { shownCatalog = v.Catalog; FillGames(); }
        Raise();
    }

    public void Raise()
    {
        PropertyChanged?.Invoke(this, new(string.Empty));
        Changed?.Invoke();
    }

    // ───────── Overview ─────────

    public Phase Phase => View.Phase;
    public bool InGameMode => View.Phase is Phase.Evicting or Phase.InGame or Phase.Cooldown or Phase.Restoring || View.Evicted.Count > 0;

    public string Header => View.Phase switch
    {
        Phase.Paused => View.PausedUntil is { } u ? string.Format(Loc.T("Status.PausedUntil"), u.ToLocalTime().ToString("t")) : Loc.T("Status.PausedForever"),
        Phase.Evicting => string.Format(Loc.T("Status.Evicting"), View.Game),
        Phase.InGame when View.Game == Loc.T("Game.Manual") => string.Format(Loc.T("Status.Manual"), Clock(Now() - (View.Since ?? Now()))),
        Phase.InGame => string.Format(Loc.T("Status.InGame"), View.Game, Clock(Now() - (View.Since ?? Now()))),
        Phase.Cooldown when Left() is { } left => string.Format(Loc.T("Status.Grace"), View.Game, Clock(left)),
        Phase.Cooldown => Loc.T("Status.WaitVram"),
        Phase.Restoring => Loc.T("Status.Restoring"),
        _ => Loc.T("Status.Idle"),
    };

    /// <summary>Time left of the grace in Cooldown, null once it is over.</summary>
    TimeSpan? Left() => View.GoneSince is { } g && g + View.Grace - Now() is { Ticks: > 0 } l ? l : null;

    public bool HasGpu => View.Gpu is not null;
    public string GpuCaption => View.Gpu is { } g
        ? string.Format(Loc.T("Overview.Gpu"), g.Name.Replace("NVIDIA GeForce ", ""), Engine.Gb(g.Total), Engine.Gb(g.Free))
        : applied ? Loc.T("Overview.NoNvml") : ""; // before the first picture nothing is known yet
    public bool NoModels => Models.Count == 0;
    public string LastEvent => View.LastEvent ?? Loc.T("Overview.NoEvents");
    public bool CanEvict => View.Phase is Phase.Idle;
    public bool CanRestore => View.Evicted.Count > 0 && View.Phase is Phase.InGame or Phase.Cooldown;
    public bool IsPaused => View.Phase == Phase.Paused;
    public bool Manual => View.Manual;

    public string Tooltip => View.Phase switch
    {
        Phase.Paused => $"{AppInfo.DisplayName} — {Header}",
        _ when View.Evicted.Count > 0 => $"{AppInfo.DisplayName} — {View.Game}: {Loc.T("Event.Evicted")} {Engine.Gb(View.Evicted.Sum(e => e.VramBytes))} {Loc.T("Unit.GB")}",
        _ => $"{AppInfo.DisplayName} — {Header}",
    };

    static List<ModelRow> BuildModels(View v)
    {
        var rows = new List<ModelRow>();
        var restoring = v.Phase == Phase.Restoring;
        var keys = v.Evicted.Select(e => (e.Backend, e.Label)).ToHashSet();
        foreach (var p in v.Probes.Values)
            foreach (var s in p.Loaded.Where(s => !keys.Contains((s.Backend, s.Label))))
                rows.Add(Row(s, s.Skip is { } why ? string.Format(Loc.T("Model.Skip"), why) : Loc.T("Model.Loaded"), s.Skip is null ? Status.Loaded : Status.Skip));
        foreach (var s in v.Evicted)
            rows.Add(Row(s,
                restoring ? Loc.T("Model.Returning")
                : !s.Restore ? Loc.T("Model.OutNoRestore")
                : v.Phase == Phase.Cooldown ? Loc.T("Model.OutSoon")
                : Loc.T("Model.Out"),
                restoring ? Status.Busy : Status.Out));
        foreach (var s in v.Failed) rows.Add(Row(s, string.Format(Loc.T("Model.Error"), s.Skip), Status.Error));
        return rows;
    }

    static ModelRow Row(Snap s, string status, Status kind) => new(
        s.Backend == "command" ? Loc.T("Models.Commands") : s.Backend switch { "ollama" => "Ollama", "comfyui" => "ComfyUI", _ => s.Backend },
        s.Label, s.VramBytes > 0 ? $"{Engine.Gb(s.VramBytes)} {Loc.T("Unit.GB")}" : "—", status, kind);

    /// <summary>Models by backend, the game, everything else, free (NVML total; PDH for our pids).</summary>
    static List<Seg> BuildBar(View v)
    {
        if (v.Gpu is not { } g) return [];
        var segs = new List<Seg>();
        var models = 0L;
        foreach (var (kind, label, fill) in new[] { ("llama-server", "llama-server", Blue), ("ollama", "Ollama", Purple), ("comfyui", "ComfyUI", Teal) })
        {
            var bytes = v.Probes.GetValueOrDefault(kind)?.Loaded.Sum(s => s.VramBytes) ?? 0;
            if (bytes <= 0) continue;
            segs.Add(new(label, bytes, fill));
            models += bytes;
        }
        if (v.GameBytes > 0) segs.Add(new(v.Game ?? Loc.T("Overview.Game"), v.GameBytes, Orange));
        var other = Math.Max(0, g.Used - models - v.GameBytes);
        segs.Add(new(Loc.T("Overview.Other"), other, Gray));
        segs.Add(new(Loc.T("Overview.Free"), g.Free, Brushes.Transparent));
        return segs;
    }

    // ───────── Games ─────────

    public string Filter
    {
        get => filter;
        set { filter = value; FillGames(); }
    }

    void FillGames()
    {
        var c = shownCatalog ?? new([], []);
        bool Hit(Game g) => filter.Length == 0 || g.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        Games.Clear();
        foreach (var g in c.Games.Where(Hit)) Games.Add(new(g, false, false));
        Excluded.Clear();
        foreach (var x in c.Excluded.Where(x => Hit(x.Game))) Excluded.Add(new(x.Game, true, x.ByDefault));
    }

    public void LoadHistory(IEnumerable<Session> sessions)
    {
        Sessions.Clear();
        foreach (var s in sessions) Sessions.Add(new(s));
        Raise();
    }

    public bool NoSessions => Sessions.Count == 0;

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

}
