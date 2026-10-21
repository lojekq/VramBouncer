using System.IO;
using System.Runtime.InteropServices;
using VramBouncer.Shell;

namespace VramBouncer.Features;

/// <summary>A program that may be a game: shown on the Games page with "It's a game" / "Not a game".</summary>
public sealed record GameHint(string Exe, long VramBytes)
{
    public string Name => Path.GetFileNameWithoutExtension(Exe);
}

/// <summary>
/// "Is this a game?" (v1.0): the foreground program holds more than 1.5 GB of dedicated VRAM for 20 s, is not a known game,
/// not excluded and not a model server. One hint per exe per run; "Not a game" silences it for good.
/// </summary>
public sealed partial class HintWatch
{
    public const long Threshold = 3L << 29; // 1.5 GB
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);

    /// <summary>GPU-heavy programs that are not games: model servers, shells, browsers, creative tools people mark themselves.</summary>
    static readonly string[] Never =
    [
        "llama-server", "ollama", "ollama app", "python", "pythonw", "LM Studio", "lms", "node", "dwm", "explorer",
        "chrome", "msedge", "firefox", "Code", "Claude", "WindowsTerminal", "obs64", "VramBouncer",
    ];

    (string Exe, DateTime Since)? heavy;
    readonly HashSet<string> hinted = new(StringComparer.OrdinalIgnoreCase);

    public List<GameHint> Hints { get; } = [];

    /// <summary>One step, only while no game runs. Returns a new hint (for the notification) or null.</summary>
    public GameHint? Step(GameMatcher matcher, long total, DateTime now)
    {
        var pid = TopWindows.Pid(GetForegroundWindow());
        var exe = pid > 0 && pid != Environment.ProcessId ? ProcessInfo.ExePath(pid) : null;
        if (exe is null || hinted.Contains(exe) || !Candidate(exe, matcher, Settings.Current.Games.NotGames)) { heavy = null; return null; }
        var bytes = VramMonitor.PerProcess([pid], total).GetValueOrDefault(pid);
        if (bytes < Threshold) { heavy = null; return null; }
        if (heavy?.Exe != exe) { heavy = (exe, now); return null; }
        if (now - heavy.Value.Since < Hold) return null;
        hinted.Add(exe);
        var hint = new GameHint(exe, bytes);
        Hints.Add(hint);
        return hint;
    }

    public void Forget(string exe) => Hints.RemoveAll(h => string.Equals(h.Exe, exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>Not a known or excluded game, not answered "Not a game", not on the never list.</summary>
    internal static bool Candidate(string exe, GameMatcher matcher, IEnumerable<string> notGames) =>
        matcher.Match(exe, Path.GetFileNameWithoutExtension(exe)) is null
        && !matcher.Excluded(exe)
        && !notGames.Contains(exe, StringComparer.OrdinalIgnoreCase)
        && !Never.Contains(Path.GetFileNameWithoutExtension(exe), StringComparer.OrdinalIgnoreCase);

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
}
