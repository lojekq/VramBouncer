using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using VramBouncer.Shell;

namespace VramBouncer.Features;

/// <summary>
/// The one background loop (spec §5): every 2 s poll games, step the state machine, run its actions one after another.
/// UI requests are queued and run inside the loop too, so nothing here races. The view goes to <see cref="Vm"/> on the UI thread.
/// </summary>
public sealed class Engine
{
    const long GB = 1L << 30;
    public static Engine? I { get; private set; }

    readonly ConcurrentQueue<Func<CancellationToken, Task>> requests = new();
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource stop = new();
    readonly GameWatcher watcher = new();
    readonly HintWatch hints = new();
    long? freeBefore; // free VRAM right before the eviction, models still loaded

    BState state = new();
    List<Snap> evicted = [];
    List<Snap> failed = [];
    Guard? guard;
    Session? session;
    GameCatalog.Result catalog = new([], []);
    GameMatcher matcher = new([], [], []);
    DateTime catalogAt;
    bool manual;
    string? lastEvent;
    Dictionary<string, Probe> probes = [];
    DateTime probedAt;
    List<(int Pid, Game Game)> running = [];

    public static void Start()
    {
        I = new Engine();
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) I.Post(_ => { I.watcher.Reset(); return Task.CompletedTask; }); };
        App.Exiting += () => I.stop.Cancel();
        _ = Task.Run(I.Loop);
    }

    // ───────── requests from the UI ─────────

    /// <summary>Runs f inside the loop; the task completes with its result (or exception).</summary>
    public Task<T> Run<T>(Func<CancellationToken, Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async ct =>
        {
            try { tcs.SetResult(await f(ct)); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public void Post(Func<CancellationToken, Task> f)
    {
        requests.Enqueue(f);
        wake.Release();
    }

    public void Command(Cmd cmd, DateTime? until = null) => Post(ct => Apply(Input(cmd, until), ct));
    public bool Manual => manual;
    public void SetManual(bool on) => Post(ct => { manual = on; return Step(ct); });
    public void RestoreNow(Action? then = null) => Post(async ct =>
    {
        manual = false;
        await Apply(Input(Cmd.RestoreNow), ct);
        if (then is not null) _ = Application.Current.Dispatcher.BeginInvoke(then);
    });
    public void RefreshCatalog() => Post(ct => { catalogAt = default; probedAt = default; return Step(ct); });
    /// <summary>The hint was answered (added as a game or "Not a game").</summary>
    public void Forget(string exe) => Post(ct => { hints.Forget(exe); catalogAt = default; return Step(ct); });
    public bool HasEvicted => evicted.Count > 0;

    /// <summary>MLink is installed and its model is not handled by its own commands yet.</summary>
    public static bool MLinkOffer => MLink.Detect() is not null && !Settings.Current.Backends.Commands.Any(MLink.Is);

    /// <summary>One click: MLink's off --hold / restore for its model, and the llama-server backend leaves :8080 alone.</summary>
    public static void UseMLink()
    {
        if (MLink.Detect() is not { } py) return;
        var b = Settings.Current.Backends;
        if (!b.Commands.Any(MLink.Is)) b.Commands.Add(MLink.Command(py));
        if (b.Llama.Port == MLink.Port) b.Llama.On = false;
        Settings.Save();
        I?.RefreshCatalog();
    }

    /// <summary>"Check eviction": what would be done now, nothing is run.</summary>
    public Task<List<string>> Plan() => Run(async ct =>
    {
        var lines = new List<string>();
        foreach (var b in Backends())
        {
            var p = await SafeProbe(b, ct, checks: true);
            foreach (var s in p.Loaded)
                lines.Add(s.Skip is { } why ? string.Format(Loc.T("Plan.Skip"), b.Title, s.Label, why)
                    : s.Backend == "command" ? string.Format(Loc.T("Plan.Command"), b.Title)
                    : string.Format(Loc.T(s.Backend == "llama-server" ? "Plan.Kill" : "Plan.Unload"), b.Title, s.Label, Gb(s.VramBytes))
                      + (s.Restore ? "" : " · " + Loc.T("Plan.NoRestore")));
            if (!p.Found && p.Warnings.Count == 0) lines.Add(string.Format(Loc.T("Plan.NotFound"), b.Title));
            lines.AddRange(p.Warnings.Select(w => $"{b.Title}: {w}"));
        }
        if (lines.Count == 0) lines.Add(Loc.T("Plan.Nothing"));
        return lines;
    });

    /// <summary>"Check" of one of My commands: runs only its check command.</summary>
    public Task<bool?> Check(UserCommand c) => Run(ct => new CommandBackend(c).IsLoadedAsync(new() { Backend = "command" }, ct));

    // ───────── the loop ─────────

    async Task Loop()
    {
        var ct = stop.Token;
        try { await Init(ct); } catch (Exception e) when (!ct.IsCancellationRequested) { Log.Error("start-up check failed", e); }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (requests.TryDequeue(out var r)) await r(ct);
                await Step(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.Error("engine step failed", e); }
            try { await wake.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { }
        }
    }

    async Task Init(CancellationToken ct)
    {
        Catalog();
        if (MLinkOffer && !Settings.Current.MLinkOffered)
        {
            Settings.Current.MLinkOffered = true;
            Settings.Save();
            Notify(true, Loc.T("MLink.Notify"), "models");
        }
        var saved = Store.Load();
        if (saved is { Evicted.Count: > 0 })
        {
            var known = new Dictionary<Snap, bool?>();
            foreach (var s in saved.Evicted) known[s] = BackendFor(s) is { } b ? await b.IsLoadedAsync(s, ct) : true;
            running = watcher.Poll(matcher);
            var (st, left) = Bouncer.Start(saved.Evicted, s => known[s], GameName(), DateTime.UtcNow, Timings());
            (state, evicted) = (st, left);
            if (left.Count > 0)
            {
                guard = new Guard { Evicted = evicted };
                session = new Session { Game = saved.Game ?? "?", Start = saved.Since ?? DateTime.UtcNow, Evicted = Models(evicted), Result = "pending" };
                Event(string.Format(Loc.T("Event.Recovered"), left.Count));
                Log.Info($"start-up: {left.Count} item(s) from state.json still out");
            }
            Save();
        }
        await Publish(ct, force: true);
    }

    async Task Step(CancellationToken ct)
    {
        if (DateTime.UtcNow - catalogAt > TimeSpan.FromMinutes(30)) Catalog();
        running = watcher.Poll(matcher);
        Announce();
        if (state.Phase == Phase.Idle && running.Count == 0 && !manual && VramMonitor.Read() is { } gpu
            && hints.Step(matcher, gpu.Total, DateTime.UtcNow) is { } hint)
        {
            Log.Info("a program with a lot of VRAM in the foreground: hint shown");
            Notify(true, string.Format(Loc.T("Hint.Notify"), hint.Name, Gb(hint.VramBytes)), "games");
            PublishNow();
        }
        await Apply(Input(Cmd.None), ct);
        await Publish(ct);
    }

    Input Input(Cmd cmd, DateTime? until = null)
    {
        var gpu = VramMonitor.Read();
        var needed = state.Phase == Phase.Cooldown // only there it matters, and a port check per item is cheap
            ? Bouncer.Needed(evicted.Select(e => (e.VramBytes, Back(e))), (long)(Settings.Current.Timings.MarginGb * GB), freeBefore)
            : 0;
        return new(DateTime.UtcNow, GameName(), gpu?.Free, needed, cmd, until);
    }

    /// <summary>Already running again (a watchdog, MLink's tray): its VRAM is taken, waiting for it would last the whole 10 minutes.</summary>
    static bool Back(Snap s) => s.Backend switch
    {
        "llama-server" when s.Instance is null => TcpPorts.ListenerPid(s.Port ?? 0) is not null,
        "command" when Settings.Current.Backends.Commands.FirstOrDefault(c => c.Name == s.Model) is { } c && MLink.Is(c)
            => TcpPorts.ListenerPid(MLink.Port) is not null,
        _ => false,
    };

    string? GameName() => running.Count > 0 ? running[0].Game.Name : manual ? Loc.T("Game.Manual") : null;

    async Task Apply(Input input, CancellationToken ct)
    {
        var before = state.Phase;
        var (s, acts) = Bouncer.Next(state, input, Timings());
        state = s;
        foreach (var a in acts)
            try
            {
                switch (a)
                {
                    case Act.Evict: await Evict(ct); break;
                    case Act.Restore or Act.RestoreLate: await Restore(a == Act.RestoreLate, ct); break;
                    case Act.Guard: await GuardStep(ct); break;
                }
            }
            finally // even after a failure (state.json not writable): never stuck in Evicting or Restoring
            {
                if (a is Act.Evict or Act.Restore or Act.RestoreLate) (state, _) = Bouncer.Next(state, input with { Now = DateTime.UtcNow, Cmd = Cmd.Done }, Timings());
            }
        if (state.Phase != before)
        {
            Log.Info($"{before} -> {state.Phase}");
            Save();
            await Publish(ct, force: true);
        }
    }

    // ───────── actions ─────────

    async Task Evict(CancellationToken ct)
    {
        PublishNow(); // "Evicting…" on screen while it runs
        freeBefore = VramMonitor.Read()?.Free;
        var skipped = new List<Snap>();
        var plan = new List<(Backend B, Snap S)>();
        foreach (var b in Backends()) // llama-server first: the biggest one goes within the first seconds
            foreach (var s in (await SafeProbe(b, ct, checks: true)).Loaded)
                if (s.Skip is null) plan.Add((b, s));
                else skipped.Add(s);
        var budget = BudgetBytes(); // v1.0: only what the game's budget needs, the biggest first
        var chosen = Budget.Choose(plan.Select(x => x.S).ToList(), VramMonitor.Read()?.Free, budget).ToHashSet();
        evicted = [];
        failed = [];
        foreach (var (b, s) in plan.Where(x => chosen.Contains(x.S)))
            {
                evicted.Add(s);
                Save(); // the snapshot is on disk before this unload
                try
                {
                    var used = s.VramBytes <= 0 ? VramMonitor.Read()?.Used : null; // unknown size (my commands, router): measure it
                    var r = await b.EvictAsync(s, ct);
                    if (used is { } u)
                    {
                        await Task.Delay(1000, ct);
                        if (VramMonitor.Read() is { } g) r = r with { VramBytes = Math.Max(0, u - g.Used) };
                    }
                    evicted[^1] = r;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Log.Warn($"{b.Kind}: eviction failed ({e.GetType().Name})"); // the message may hold paths or command lines: not for logs
                    evicted.RemoveAt(evicted.Count - 1);
                    failed.Add(s with { Skip = e.Message });
                    Notify(Settings.Current.Notify.Errors, string.Format(Loc.T("Notify.EvictFailed"), b.Title, e.Message));
                }
                Save();
            }
        var done = evicted;
        var keys = done.Select(d => (d.Backend, d.Label)).ToHashSet();
        guard = new Guard { Evicted = evicted, MayEvict = budget > 0 ? s => keys.Contains((s.Backend, s.Label)) : _ => true };
        foreach (var s in skipped) if (s.Pid is { } pid) guard.Reported.Add(pid);
        session = new Session { Game = state.Game ?? "?", Start = DateTime.UtcNow, Evicted = Models(done), Result = "pending" };
        Log.Info($"evicted {done.Count} item(s), {Gb(done.Sum(d => d.VramBytes))} GB");
        var text = done.Count == 0 ? Loc.T("Notify.NothingLoaded")
            : string.Join(", ", done.Select(d => $"{d.Label} ({Title(d)}{(d.VramBytes > 0 ? ", " + Gb(d.VramBytes) + " " + Loc.T("Unit.GB") : "")})"));
        Event($"{state.Game} → {Loc.T("Event.Evicted")}: {text}");
        if (done.Count > 0) Notify(Settings.Current.Notify.Evict, $"{state.Game} → {Loc.T("Event.Evicted")}: {text}", "overview");
    }

    async Task Restore(bool late, CancellationToken ct)
    {
        PublishNow();
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var errors = new List<string>();
        if (late && VramMonitor.Read() is { } gpu) warnings.Add(string.Format(Loc.T("Restore.Busy"), Gb(gpu.Used)));
        foreach (var s in evicted.ToList()) // in backend order, strictly one by one: llama-server's --fit must see the real free VRAM
        {
            if (s.Restore && BackendFor(s) is { On: true } b)
                try { if (await b.RestoreAsync(s, ct) is { } w) warnings.Add(w); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Log.Warn($"{s.Backend}: restore failed ({e.GetType().Name})");
                    errors.Add($"{s.Label}: {e.Message}");
                }
            evicted.Remove(s);
            Save();
        }
        guard = null;
        failed = [];
        var secs = Math.Round(sw.Elapsed.TotalSeconds);
        if (session is not null)
        {
            session.End = DateTime.UtcNow;
            session.RestoreSec = secs;
            session.Result = errors.Count > 0 ? "error" : warnings.Count > 0 ? "warning" : "ok";
            session.Note = string.Join("; ", errors.Concat(warnings)) is { Length: > 0 } n ? n : null;
            Store.Append(session);
            var guardCount = session.Guard.Count;
            var text = string.Format(Loc.T("Notify.Summary"), session.Game, SessionRow.Minutes(session.End.Value - session.Start),
                Gb(session.Evicted.Sum(m => m.Bytes)), secs) + (guardCount > 0 ? " · " + string.Format(Loc.T("Notify.GuardCount"), guardCount) : "");
            var back = session.Back.LastOrDefault(); // "a model came back mid-game" outweighs the count
            var summary = back is null ? text : text + "\n" + back;
            Event(text);
            if (errors.Count > 0) Notify(Settings.Current.Notify.Errors, string.Join("\n", errors), "journal");
            else if (warnings.Count > 0) Notify(Settings.Current.Notify.Errors, string.Join("\n", warnings), "journal");
            else Notify(Settings.Current.Notify.Restore || guardCount > 0 && Settings.Current.Notify.Summary, summary, "journal");
            session = null;
        }
        Log.Info($"restore done in {secs} s, {errors.Count} error(s), {warnings.Count} warning(s)");
    }

    async Task GuardStep(CancellationToken ct)
    {
        if (guard is null) return;
        foreach (var b in Backends())
        {
            try
            {
                foreach (var e in await b.GuardAsync(guard, ct))
                {
                    session?.Guard.Add(e);
                    Event(e);
                    Log.Info($"guard: {b.Kind} acted");
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.Warn($"{b.Kind}: guard failed ({e.GetType().Name})"); }
        }
        foreach (var w in guard.Warnings) Notify(Settings.Current.Notify.Errors, w, "models"); // muted in full screen: the summary repeats it
        session?.Back.AddRange(guard.Warnings);
        guard.Warnings.Clear();
    }

    // ───────── helpers ─────────

    List<Backend> Backends()
    {
        var c = Settings.Current.Backends;
        var list = new List<Backend> { new LlamaBackend(c.Llama), new LmStudioBackend(c.LmStudio), new OllamaBackend(c.Ollama), new ComfyBackend(c.Comfy) };
        list.AddRange(c.Commands.Select(u => new CommandBackend(u)));
        return list.Where(b => b.On).ToList();
    }

    Backend? BackendFor(Snap s)
    {
        var c = Settings.Current.Backends;
        return s.Backend switch
        {
            "ollama" => new OllamaBackend(c.Ollama),
            "llama-server" => new LlamaBackend(c.Llama),
            "comfyui" => new ComfyBackend(c.Comfy),
            "lmstudio" => new LmStudioBackend(c.LmStudio),
            "command" => c.Commands.FirstOrDefault(u => u.Name == s.Model) is { } u ? new CommandBackend(u) : null,
            _ => null,
        };
    }

    static string Title(Snap s) => s.Backend == "command" ? Loc.T("Models.Commands") : s.Backend switch
    {
        "ollama" => "Ollama", "llama-server" => "llama-server", "comfyui" => "ComfyUI", "lmstudio" => "LM Studio", _ => s.Backend,
    };

    /// <summary>The running game's VRAM budget in bytes; 0 = evict everything.</summary>
    long BudgetBytes() =>
        running.Count > 0 && Settings.Current.Games.Budgets.TryGetValue(running[0].Game.Root, out var gb) && gb > 0 ? (long)(gb * GB) : 0;

    static async Task<Probe> SafeProbe(Backend b, CancellationToken ct, bool checks)
    {
        if (!checks && b is CommandBackend) return new(true, null, [], []); // a check runs a process: not for the 2 s view refresh
        try { return await b.ProbeAsync(ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"{b.Kind}: probe failed ({e.GetType().Name})");
            return Probe.None(e.Message);
        }
    }

    static Timings Timings() => Features.Timings.From(Settings.Current.Timings);
    static List<SessionModel> Models(IEnumerable<Snap> s) => s.Select(x => new SessionModel(x.Backend, x.Label, x.VramBytes)).ToList();
    public static string Gb(long bytes) => (bytes / (double)GB).ToString("0.0");

    void Save()
    {
        try { Store.Save(new SavedState(state.Phase, state.Game, state.Since, evicted)); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Error("state.json not written", e);
            if (state.Phase == Phase.Evicting) throw; // no snapshot on disk, no unload
        }
    }

    void Catalog()
    {
        catalog = GameCatalog.Build(Settings.Current.Games);
        matcher = GameCatalog.Matcher(catalog, Settings.Current.Games);
        watcher.Reset();
        catalogAt = DateTime.UtcNow;
        Log.Info($"catalog: {catalog.Games.Count} games, {catalog.Excluded.Count} excluded");
    }

    /// <summary>"Recognized as a game: X" the first time a game root is seen running (a click opens Games with "Not a game").</summary>
    void Announce()
    {
        var known = Settings.Current.Games.Known;
        foreach (var g in running.Select(r => r.Game).DistinctBy(g => g.Root))
        {
            if (known.Contains(g.Root, StringComparer.OrdinalIgnoreCase)) continue;
            known.Add(g.Root);
            Settings.Save();
            Notify(true, string.Format(Loc.T("Notify.Recognized"), g.Name), "games");
        }
    }

    void Event(string text) => lastEvent = $"{DateTime.Now:HH:mm} {text}";

    static void Notify(bool on, string text, string? page = null)
    {
        if (!on) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Tray.Notify(AppInfo.DisplayName, text, page));
    }

    /// <summary>Probes for the view: every 2 s while the window is open, every 30 s otherwise (tray tooltip).</summary>
    async Task Publish(CancellationToken ct, bool force = false)
    {
        var due = DateTime.UtcNow - probedAt > TimeSpan.FromSeconds(Vm.I.WindowVisible ? 2 : 30);
        if (!due && !force) return;
        if (due)
        {
            var p = new Dictionary<string, Probe>();
            var c = Settings.Current.Backends;
            foreach (var b in new Backend[] { new LlamaBackend(c.Llama), new LmStudioBackend(c.LmStudio), new OllamaBackend(c.Ollama), new ComfyBackend(c.Comfy) })
                p[b.Kind] = b.On ? await SafeProbe(b, ct, checks: false) : Probe.None();
            probes = p;
            probedAt = DateTime.UtcNow;
        }
        PublishNow();
    }

    /// <summary>On the loop thread: the picture is built here, the UI thread only gets the immutable copy.</summary>
    void PublishNow()
    {
        var gpu = VramMonitor.Read();
        var gamePids = running.Select(r => r.Pid).ToList();
        var gameBytes = gpu is { } g0 ? VramMonitor.PerProcess(gamePids, g0.Total).Values.Sum() : 0;
        var view = new View
        {
            Phase = state.Phase, Game = state.Game ?? GameName(), Since = state.Since, GoneSince = state.GoneSince, PausedUntil = state.PausedUntil,
            Grace = Timings().Grace, Manual = manual, Gpu = gpu, GameBytes = gameBytes, Evicted = [.. evicted], Failed = [.. failed],
            Probes = probes, LastEvent = lastEvent, Catalog = catalog, Hints = [.. hints.Hints],
        };
        Application.Current?.Dispatcher.BeginInvoke(() => Vm.I.Apply(view));
    }
}

/// <summary>Everything the window and the tray show, as one immutable picture (the demo builds these by hand).</summary>
public sealed record View
{
    public Phase Phase { get; init; }
    public string? Game { get; init; }
    public DateTime? Since { get; init; }
    public DateTime? GoneSince { get; init; }
    public DateTime? PausedUntil { get; init; }
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(60);
    public bool Manual { get; init; }
    public GpuMemory? Gpu { get; init; }
    public long GameBytes { get; init; }
    public List<Snap> Evicted { get; init; } = [];
    public List<Snap> Failed { get; init; } = [];
    public Dictionary<string, Probe> Probes { get; init; } = [];
    public string? LastEvent { get; init; }
    public GameCatalog.Result Catalog { get; init; } = new([], []);
    public List<GameHint> Hints { get; init; } = [];
}
