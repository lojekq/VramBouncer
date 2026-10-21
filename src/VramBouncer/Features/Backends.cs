using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VramBouncer.Shell;

namespace VramBouncer.Features;

/// <summary>One evicted (or evictable) model. Written to state.json before anything is unloaded.</summary>
public sealed record Snap
{
    public required string Backend { get; init; }  // ollama | llama-server | comfyui | command
    public string? Model { get; init; }            // Ollama model, command name, gguf name for llama-server
    public string? Exe { get; init; }
    public string? Args { get; init; }
    public string? WorkDir { get; init; }
    public int? Port { get; init; }
    public int? Pid { get; init; }
    public int? ContextLength { get; init; }
    public long? KeepAlive { get; init; }          // Ollama, seconds; -1 = forever; null = Ollama's default
    public long VramBytes { get; init; }
    public bool Restore { get; init; }
    public DateTime? KilledAt { get; init; }       // llama-server, UTC: for the kill-loop check
    [JsonIgnore] public string? Skip { get; init; } // loaded but not evicted, and why (shown in the UI)

    [JsonIgnore] public string Label => Model ?? Backend;
}

/// <summary>Found = the server answers. Detail is shown in the UI only (a command line): never logged.</summary>
public sealed record Probe(bool Found, string? Version, List<Snap> Loaded, List<string> Warnings, string? Detail = null)
{
    public static Probe None(params string[] warnings) => new(false, null, [], [.. warnings]);
}

/// <summary>A local model server. All calls run one after another on the engine loop.</summary>
public abstract class Backend
{
    public abstract string Kind { get; }
    public abstract string Title { get; }
    public abstract bool On { get; }
    public abstract Task<Probe> ProbeAsync(CancellationToken ct);
    /// <summary>Unloads one item of the probe; returns it with what was learned (kill time). Throws on failure.</summary>
    public abstract Task<Snap> EvictAsync(Snap s, CancellationToken ct);
    /// <summary>Brings it back; returns a warning or null. Throws on failure.</summary>
    public virtual Task<string?> RestoreAsync(Snap s, CancellationToken ct) => Task.FromResult<string?>(null);
    /// <summary>During a game: unloads again what came back; returns journal events.</summary>
    public abstract Task<List<string>> GuardAsync(Guard g, CancellationToken ct);
    /// <summary>For the start-up check of state.json: null = can't tell.</summary>
    public virtual Task<bool?> IsLoadedAsync(Snap s, CancellationToken ct) => Task.FromResult<bool?>(null);
}

/// <summary>What the guard needs across ticks of one game session.</summary>
public sealed class Guard
{
    public required IReadOnlyList<Snap> Evicted { get; init; }
    public HashSet<int> Reported { get; } = [];
    public DateTime LastComfyFree { get; set; }
    public List<string> Warnings { get; } = []; // also go to a notification
}

public static class Http
{
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    public static readonly TimeSpan Short = TimeSpan.FromSeconds(10), Load = TimeSpan.FromMinutes(5);

    public static async Task<HttpResponseMessage> Send(HttpMethod m, string url, JsonNode? body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var r = await Client.SendAsync(req, cts.Token);
        await r.Content.LoadIntoBufferAsync(cts.Token);
        return r;
    }

    public static async Task<JsonNode?> GetJson(string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var r = await Send(HttpMethod.Get, url, null, timeout ?? Short, ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A base URL of a loopback address, or null (spec §4: no remote servers).</summary>
    public static string? Loopback(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.IsLoopback ? u.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>OLLAMA_HOST forms: "0.0.0.0", "127.0.0.1:11434", "http://localhost:11434", ":11434".</summary>
    /// <summary>Same rules as Ollama's envconfig.Host: no scheme → port 11434, http:// → 80, https:// → 443; 0.0.0.0 → 127.0.0.1.</summary>
    public static string OllamaDefault(string? env)
    {
        var s = (env ?? "").Trim().TrimEnd('/');
        var (scheme, port) = ("http", "11434");
        if (s.IndexOf("://") is var i and >= 0) (scheme, port, s) = (s[..i], s[..i] == "https" ? "443" : "80", s[(i + 3)..]);
        var host = s;
        if (s.LastIndexOf(':') is var c and >= 0 && !s.EndsWith(']')) (host, port) = (s[..c], s[(c + 1)..]);
        if (host is "" or "0.0.0.0" or "[::]" or "::") host = "127.0.0.1";
        return $"{scheme}://{host}:{port}";
    }

    /// <summary>Something listens on the URL's port. A refused connect costs ~2 s on Windows (SYN retries), so check first.</summary>
    public static bool Listening(string url) => TcpPorts.ListenerPid(new Uri(url).Port) is not null;

    public static async Task<bool> Healthy(int port, CancellationToken ct)
    {
        try { using var r = await Send(HttpMethod.Get, $"http://127.0.0.1:{port}/health", null, TimeSpan.FromSeconds(3), ct); return r.IsSuccessStatusCode; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
}

// ───────────────────────── Ollama ─────────────────────────

public static class OllamaRestorePolicy
{
    /// <summary>
    /// Whether to load the model again after the game and with which keep_alive (seconds, -1 = forever, null = Ollama's default).
    /// A model with ≤ 30 min left would have gone anyway: Ollama loads it on the next request.
    /// </summary>
    public static (bool Restore, long? KeepAlive) Decide(DateTimeOffset expiresAt, DateTimeOffset now, string policy)
    {
        var left = expiresAt - now;
        if (policy == RestorePolicy.Never) return (false, null);
        if (left > TimeSpan.FromDays(365)) return (true, -1);              // keep_alive -1: expires_at is centuries ahead
        if (left > TimeSpan.FromMinutes(30)) return (true, (long)left.TotalSeconds);
        return policy == RestorePolicy.Always ? (true, null) : (false, null);
    }
}

public sealed class OllamaBackend(OllamaConfig cfg) : Backend
{
    public override string Kind => "ollama";
    public override string Title => "Ollama";
    public override bool On => cfg.On;
    public string? Url => Http.Loopback(cfg.Url ?? Http.OllamaDefault(Environment.GetEnvironmentVariable("OLLAMA_HOST")));

    public override async Task<Probe> ProbeAsync(CancellationToken ct)
    {
        if (Url is not { } url) return Probe.None(Loc.T("Models.NotLoopback"));
        if (!Http.Listening(url)) return Probe.None();
        try
        {
            var version = (await Http.GetJson(url + "/api/version", ct))?["version"]?.GetValue<string>();
            var loaded = await Loaded(url, ct);
            return new(true, version, loaded, []);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException && !ct.IsCancellationRequested) { return Probe.None(); }
    }

    async Task<List<Snap>> Loaded(string url, CancellationToken ct) => Parse(await Http.GetJson(url + "/api/ps", ct), DateTimeOffset.Now, cfg.Restore);

    /// <summary>GET /api/ps → snapshots. An expires_at that does not parse counts as "soon": never restore with keep_alive -1 by mistake.</summary>
    internal static List<Snap> Parse(JsonNode? ps, DateTimeOffset now, string policy)
    {
        var list = new List<Snap>();
        foreach (var m in ps?["models"]?.AsArray() ?? [])
        {
            if (m?["name"]?.GetValue<string>() is not { } name) continue;
            // Go writes up to 9 fraction digits, .NET reads 7
            var text = Regex.Replace(m["expires_at"]?.GetValue<string>() ?? "", @"(\.\d{7})\d+", "$1");
            var expires = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : now;
            var (restore, keep) = OllamaRestorePolicy.Decide(expires, now, policy);
            list.Add(new()
            {
                Backend = "ollama", Model = name, ContextLength = m["context_length"]?.GetValue<int>(),
                VramBytes = m["size_vram"]?.GetValue<long>() ?? 0, Restore = restore, KeepAlive = keep,
            });
        }
        return list;
    }

    public override async Task<Snap> EvictAsync(Snap s, CancellationToken ct)
    {
        var url = Url ?? throw new InvalidOperationException(Loc.T("Models.NotLoopback"));
        using var r = await Http.Send(HttpMethod.Post, url + "/api/generate", new JsonObject { ["model"] = s.Model, ["keep_alive"] = 0 }, Http.Short, ct);
        r.EnsureSuccessStatusCode();
        for (var i = 0; i < 20 && (await Loaded(url, ct)).Any(l => l.Model == s.Model); i++) await Task.Delay(500, ct); // unload is asynchronous
        return s;
    }

    public override async Task<string?> RestoreAsync(Snap s, CancellationToken ct)
    {
        var url = Url ?? throw new InvalidOperationException(Loc.T("Models.NotLoopback"));
        var body = new JsonObject { ["model"] = s.Model };
        if (s.KeepAlive is { } k) body["keep_alive"] = k; // a number: Ollama rejects the string "-1"
        if (s.ContextLength is { } c) body["options"] = new JsonObject { ["num_ctx"] = c };
        using (var r = await Http.Send(HttpMethod.Post, url + "/api/generate", body, Http.Load, ct)) r.EnsureSuccessStatusCode();
        var ps = (await Http.GetJson(url + "/api/ps", ct))?["models"]?.AsArray().FirstOrDefault(m => m?["name"]?.GetValue<string>() == s.Model);
        if (ps is null) return string.Format(Loc.T("Restore.NotLoaded"), s.Model);
        return ps["size_vram"]?.GetValue<long>() < ps["size"]?.GetValue<long>() ? string.Format(Loc.T("Restore.Partial"), s.Model) : null;
    }

    public override async Task<List<string>> GuardAsync(Guard g, CancellationToken ct)
    {
        var events = new List<string>();
        if (Url is not { } url || !Http.Listening(url)) return events;
        List<Snap> loaded;
        try { loaded = await Loaded(url, ct); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return events; }
        foreach (var m in loaded)
        {
            await EvictAsync(m, ct);
            events.Add(string.Format(Loc.T("Guard.Reloaded"), m.Model));
        }
        return events;
    }

    public override async Task<bool?> IsLoadedAsync(Snap s, CancellationToken ct)
    {
        if (Url is not { } url) return null;
        if (!Http.Listening(url)) return false;
        try { return (await Loaded(url, ct)).Any(l => l.Model == s.Model); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }
}

// ───────────────────────── llama-server ─────────────────────────

/// <summary>llama-server arguments on top of the template's CmdLine.</summary>
public static class LlamaArgs
{
    public static int Port(string[] args) => Value(args, "--port") is { } p && int.TryParse(p, out var n) ? n : 8080;

    public static string? Model(string[] args) => Value(args, "-m") ?? Value(args, "--model");

    /// <summary>Values of -m, --model, --mmproj and every *-file option that are not absolute paths.</summary>
    public static List<string> RelativePaths(string[] args)
    {
        var r = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var (name, value) = Split(args, ref i);
            if (name is "-m" or "--model" or "--mmproj" || name.StartsWith("--") && name.EndsWith("-file"))
                if (value is { Length: > 0 } v && !Path.IsPathFullyQualified(v)) r.Add(v);
        }
        return r;
    }

    /// <summary>Adds --log-file unless it is already there, so a relaunched server writes into our logs folder.</summary>
    public static string WithLogFile(string rawArgs, string logFile)
    {
        var args = CmdLine.Args(rawArgs);
        if (args.Any(a => a == "--log-file" || a.StartsWith("--log-file="))) return rawArgs;
        return $"{rawArgs} --log-file \"{logFile}\"".TrimStart();
    }

    static string? Value(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var (name, value) = Split(args, ref i);
            if (name == option) return value;
        }
        return null;
    }

    static (string Name, string? Value) Split(string[] args, ref int i)
    {
        var a = args[i];
        if (a.StartsWith("--") && a.IndexOf('=') is > 2 and var eq) return (a[..eq], a[(eq + 1)..]);
        if (!a.StartsWith('-')) return (a, null);
        return (a, i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null);
    }
}

public static class KillLoop
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>A server that came back within 60 s of our kill was restarted by a watchdog: hands off, notify.</summary>
    public static bool IsWatchdog(DateTime killedAt, DateTime startedAt) => startedAt >= killedAt && startedAt - killedAt < Window;
}

public sealed class LlamaBackend(LlamaConfig cfg) : Backend
{
    static readonly string[] Watchdogs = ["powershell.exe", "pwsh.exe", "cmd.exe", "python.exe", "pythonw.exe", "wscript.exe", "cscript.exe", "llama-swap.exe"];

    public override string Kind => "llama-server";
    public override string Title => "llama-server";
    public override bool On => cfg.On;
    public static string LogFile => Path.Combine(Log.Dir, "llama-server.log");

    /// <summary>Debug builds never touch :8080, the owner's MLink server (00-shared §9.4).</summary>
    public static bool Forbidden(int port)
    {
#if DEBUG
        return port == 8080;
#else
        return false;
#endif
    }

    public override async Task<Probe> ProbeAsync(CancellationToken ct)
    {
        var port = cfg.Port;
        if (Forbidden(port)) return Probe.None(Loc.T("Llama.DebugPort"));
        if (TcpPorts.ListenerPid(port) is not { } pid) return Probe.None();
        var exe = ProcessInfo.ExePath(pid);
        if (exe is null || !Path.GetFileName(exe).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
            return exe is null && ProcessInfo.IsElevated(pid) is not false
                ? new(true, null, [new() { Backend = Kind, Port = port, Pid = pid, Skip = Loc.T("Llama.Elevated") }], [Loc.T("Llama.Elevated")])
                : Probe.None(string.Format(Loc.T("Llama.PortTaken"), port));

        var warnings = new List<string>();
        var cmd = ProcessInfo.CommandLine(pid);
        var raw = cmd is null ? null : CmdLine.SplitExe(cmd).RawArgs;
        var args = raw is null ? [] : CmdLine.Args(raw);
        foreach (var rel in LlamaArgs.RelativePaths(args)) warnings.Add(string.Format(Loc.T("Llama.Relative"), rel));
        if (ProcessInfo.Tree().Parent(pid)?.Exe is { } parent && Watchdogs.Contains(parent, StringComparer.OrdinalIgnoreCase))
            warnings.Add(string.Format(Loc.T("Llama.Parent"), parent));
        if (cfg.HandsOff) warnings.Add(Loc.T("Llama.HandsOff"));

        // Without a command to start it again we don't kill (spec §8), unless the user said so; policy "never" is the user's choice
        var hasCommand = cmd is not null || cfg.Command is { Length: > 0 };
        var skip = cfg.HandsOff ? Loc.T("Llama.HandsOff")
            : ProcessInfo.IsElevated(pid) == true ? Loc.T("Llama.Elevated")
            : !hasCommand && !Settings.Current.KillWithoutRestore ? Loc.T("Llama.NoCommand")
            : null;
        var model = LlamaArgs.Model(args) is { } m ? Path.GetFileNameWithoutExtension(m) : "llama-server";
        var health = await Http.Healthy(port, ct);
        var snap = new Snap
        {
            Backend = Kind, Model = model, Exe = exe, Args = raw, WorkDir = Path.GetDirectoryName(exe), Port = port, Pid = pid,
            VramBytes = VramMonitor.PerProcess([pid], VramMonitor.Read()?.Total ?? 0).GetValueOrDefault(pid),
            Restore = hasCommand && cfg.Restore != RestorePolicy.Never, Skip = skip,
        };
        return new(true, health ? "ok" : "loading", [snap], warnings, cmd);
    }

    public override async Task<Snap> EvictAsync(Snap s, CancellationToken ct)
    {
        if (Forbidden(s.Port ?? 0)) throw new InvalidOperationException(Loc.T("Llama.DebugPort"));
        if (s.Pid is not { } pid || TcpPorts.ListenerPid(s.Port ?? 0) != pid) throw new InvalidOperationException(Loc.T("Llama.Gone"));
        using var p = Process.GetProcessById(pid);
        var killed = DateTime.UtcNow;
        p.Kill();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(Loc.T("Llama.NotKilled")); }
        Log.Info($"llama-server on :{s.Port} stopped");
        return s with { KilledAt = killed };
    }

    public override async Task<string?> RestoreAsync(Snap s, CancellationToken ct)
    {
        var port = s.Port ?? cfg.Port;
        if (Forbidden(port)) throw new InvalidOperationException(Loc.T("Llama.DebugPort"));
        Process? started = null;
        if (TcpPorts.ListenerPid(port) is null) // busy port: someone (MLink autostart, a watchdog) already brought it back
        {
            var (exe, args) = cfg.Command is { Length: > 0 } c ? CmdLine.SplitExe(c) : (s.Exe!, LlamaArgs.WithLogFile(s.Args ?? "", LogFile));
            Directory.CreateDirectory(Log.Dir);
            // No redirection: a pipe would break once we exit and the server would die on its next write (spec §8)
            started = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = s.WorkDir is { } d && Directory.Exists(d) ? d : Path.GetDirectoryName(exe) ?? "",
            });
            Log.Info($"llama-server on :{port} started");
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Http.Load)
        {
            if (await Http.Healthy(port, ct)) return null;
            if (started is { HasExited: true } && cfg.Command is not { Length: > 0 })
                throw new InvalidOperationException(string.Format(Loc.T("Llama.Exited"), started.ExitCode));
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException(Loc.T("Llama.NoHealth"));
    }

    public override Task<List<string>> GuardAsync(Guard g, CancellationToken ct)
    {
        var events = new List<string>();
        var port = cfg.Port;
        if (Forbidden(port) || TcpPorts.ListenerPid(port) is not { } pid || !g.Reported.Add(pid)) return Task.FromResult(events);
        var killed = g.Evicted.FirstOrDefault(e => e.Backend == Kind && e.Port == port)?.KilledAt;
        if (killed is { } k && ProcessInfo.StartTime(pid) is { } started && KillLoop.IsWatchdog(k, started))
        {
            cfg.HandsOff = true;
            Settings.Save();
            var parent = ProcessInfo.Tree().Ancestors(pid).FirstOrDefault()?.Exe ?? "?";
            var text = string.Format(Loc.T("Guard.Watchdog"), parent);
            events.Add(text);
            g.Warnings.Add(text);
            Log.Warn($"llama-server on :{port} came back within 60 s: another program restarts it");
        }
        else
        {
            var text = string.Format(Loc.T("Guard.LlamaStarted"), port);
            events.Add(text);
            g.Warnings.Add(text);
        }
        return Task.FromResult(events);
    }

    public override async Task<bool?> IsLoadedAsync(Snap s, CancellationToken ct) =>
        TcpPorts.ListenerPid(s.Port ?? cfg.Port) is not null && await Http.Healthy(s.Port ?? cfg.Port, ct);
}

// ───────────────────────── ComfyUI ─────────────────────────

public sealed class ComfyBackend(ComfyConfig cfg) : Backend
{
    public override string Kind => "comfyui";
    public override string Title => "ComfyUI";
    public override bool On => cfg.On;
    string? Url => Http.Loopback(cfg.Url);

    public override async Task<Probe> ProbeAsync(CancellationToken ct)
    {
        if (Url is not { } url) return Probe.None(Loc.T("Models.NotLoopback"));
        if (!Http.Listening(url)) return Probe.None();
        try
        {
            var stats = await Http.GetJson(url + "/system_stats", ct);
            var version = stats?["system"]?["comfyui_version"]?.GetValue<string>();
            var pid = TcpPorts.ListenerPid(new Uri(url).Port);
            var vram = pid is { } p ? VramMonitor.PerProcess([p], VramMonitor.Read()?.Total ?? 0).GetValueOrDefault(p) : 0;
            return new(true, version, [new() { Backend = Kind, Model = "ComfyUI", Pid = pid, VramBytes = vram, Restore = false }], []);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested) { return Probe.None(); }
    }

    public override async Task<Snap> EvictAsync(Snap s, CancellationToken ct)
    {
        await Free(ct);
        return s;
    }

    // The flag is handled by the queue worker: the call returns at once and is harmless to repeat
    async Task Free(CancellationToken ct)
    {
        var url = Url ?? throw new InvalidOperationException(Loc.T("Models.NotLoopback"));
        using var r = await Http.Send(HttpMethod.Post, url + "/free", new JsonObject { ["unload_models"] = true, ["free_memory"] = true }, Http.Short, ct);
        r.EnsureSuccessStatusCode();
    }

    public override async Task<List<string>> GuardAsync(Guard g, CancellationToken ct)
    {
        if (DateTime.UtcNow - g.LastComfyFree < TimeSpan.FromSeconds(60) || Url is not { } url || !Http.Listening(url)) return [];
        g.LastComfyFree = DateTime.UtcNow;
        try { await Free(ct); } catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }
        return [];
    }
}

// ───────────────────────── My commands ─────────────────────────

public sealed class CommandBackend(UserCommand cmd) : Backend
{
    public static readonly TimeSpan EvictTimeout = TimeSpan.FromMinutes(2), RestoreTimeout = TimeSpan.FromMinutes(5), CheckTimeout = TimeSpan.FromSeconds(30);

    public override string Kind => "command";
    public override string Title => cmd.Name;
    public override bool On => cmd.On && cmd.Evict.Length > 0;
    public UserCommand Command => cmd;

    Snap Snap() => new() { Backend = Kind, Model = cmd.Name, Restore = cmd.Restore.Length > 0 };

    public override async Task<Probe> ProbeAsync(CancellationToken ct)
    {
        if (cmd.Check.Length == 0) return new(true, null, [Snap()], []);
        var loaded = await IsLoadedAsync(Snap(), ct);
        return new(true, null, loaded == true ? [Snap()] : [], loaded is null ? [Loc.T("Command.CheckFailed")] : []);
    }

    public override async Task<Snap> EvictAsync(Snap s, CancellationToken ct)
    {
        var (code, err) = await CommandRunner.RunAsync(cmd.Evict, EvictTimeout, ct);
        if (code != 0) throw new InvalidOperationException(string.Format(Loc.T("Command.Failed"), cmd.Name, code, err));
        return s;
    }

    public override async Task<string?> RestoreAsync(Snap s, CancellationToken ct)
    {
        if (cmd.Restore.Length == 0) return null;
        var (code, err) = await CommandRunner.RunAsync(cmd.Restore, RestoreTimeout, ct);
        if (code != 0) throw new InvalidOperationException(string.Format(Loc.T("Command.Failed"), cmd.Name, code, err));
        return null;
    }

    public override async Task<List<string>> GuardAsync(Guard g, CancellationToken ct)
    {
        if (cmd.Check.Length == 0 || await IsLoadedAsync(Snap(), ct) != true) return [];
        await EvictAsync(Snap(), ct);
        return [string.Format(Loc.T("Guard.Reloaded"), cmd.Name)];
    }

    /// <summary>Check exit code 0 = loaded; null when there is no check or it could not run.</summary>
    public override async Task<bool?> IsLoadedAsync(Snap s, CancellationToken ct)
    {
        if (cmd.Check.Length == 0) return null;
        try { return (await CommandRunner.RunAsync(cmd.Check, CheckTimeout, ct)).Code == 0; }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}

/// <summary>
/// Runs a user command with no window. Not ConsoleTool.Run on purpose: a "restore" command starts a server that inherits the pipes,
/// ConsoleTool would wait for them to close (until its timeout) and then kill the whole tree, the new server with it.
/// Here only stderr is read (MLink puts the reason there), the exit of the process itself ends the wait,
/// and a timeout kills that process only.
/// </summary>
public static class CommandRunner
{
    public static async Task<(int Code, string Err)> RunAsync(string commandLine, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = CmdLine.SplitExe(commandLine);
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException(exe);
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = err.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observed: a closed pipe must not crash the app
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException(string.Format(Loc.T("Command.Timeout"), Path.GetFileName(exe), (int)timeout.TotalSeconds));
        }
        var text = await Task.WhenAny(err, Task.Delay(2000, CancellationToken.None)) == err && err.IsCompletedSuccessfully ? err.Result : "";
        return (p.ExitCode, text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "");
    }
}
