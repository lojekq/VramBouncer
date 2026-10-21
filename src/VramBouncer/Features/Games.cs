using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using VramBouncer.Shell;

namespace VramBouncer.Features;

/// <summary>A game root: a folder (Steam, Epic, manual folder) or one exe (manual). ExeName helps when the path of an elevated process can't be read.</summary>
public sealed record Game(string Name, string Source, string Root, string? ExeName = null, string? AppId = null);

public sealed record Excluded(Game Game, bool ByDefault);

/// <summary>Valve KeyValues text (libraryfolders.vdf, appmanifest_*.acf): quoted strings, braces, \\ and \" escapes.</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Tokens(text).GetEnumerator();
        return Block(tokens);
    }

    static Dictionary<string, object> Block(IEnumerator<string?> t)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (t.MoveNext() && t.Current is { } key) // null token = closing brace
        {
            if (!t.MoveNext()) break;
            d[key] = t.Current is null ? Block(t) : t.Current;
        }
        return d;
    }

    // Both braces come out as null: after a key it is "{" (the value is a block), in key position it is "}" (block ends)
    static IEnumerable<string?> Tokens(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '{' or '}') { yield return null; continue; }
            if (c != '"') continue;
            var sb = new StringBuilder();
            for (i++; i < s.Length && s[i] != '"'; i++)
                sb.Append(s[i] == '\\' && i + 1 < s.Length ? s[++i] : s[i]);
            yield return sb.ToString();
        }
    }

    public static Dictionary<string, object>? Child(this Dictionary<string, object> d, string key) => d.GetValueOrDefault(key) as Dictionary<string, object>;
    public static string? Str(this Dictionary<string, object> d, string key) => d.GetValueOrDefault(key) as string;
}

public static class SteamLibraryParser
{
    /// <summary>Every "path" of libraryfolders.vdf.</summary>
    public static List<string> Libraries(string vdf) =>
        Vdf.Parse(vdf).Child("libraryfolders")?.Values.OfType<Dictionary<string, object>>()
            .Select(l => l.Str("path")).OfType<string>().ToList() ?? [];

    /// <summary>appmanifest_*.acf → (appid, name, installdir); null when a field is missing.</summary>
    public static (string AppId, string Name, string InstallDir)? Manifest(string acf) =>
        Vdf.Parse(acf).Child("AppState") is { } a && a.Str("appid") is { } id && a.Str("name") is { } n && a.Str("installdir") is { Length: > 0 } dir
            ? (id, n, dir) : null;
}

public static class EpicManifestParser
{
    /// <summary>One Manifests\*.item: null for an incomplete install or an empty InstallLocation.</summary>
    public static Game? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
        if (S("InstallLocation") is not { Length: > 0 } root) return null;
        var exe = S("LaunchExecutable") is { Length: > 0 } le ? Path.GetFileName(le) : null;
        return new(S("DisplayName") ?? Path.GetFileName(root.TrimEnd('\\', '/')), "Epic", root, exe);
    }
}

/// <summary>exe path → game. Exclusions (folders and file-name patterns) win over game roots.</summary>
public sealed class GameMatcher(IReadOnlyList<Game> games, IReadOnlyList<string> excludedRoots, IReadOnlyList<string> patterns)
{
    /// <summary>Small helpers that live inside game folders and must not keep game mode on.</summary>
    public static readonly string[] DefaultPatterns =
        ["UnityCrashHandler*", "*CrashReport*", "*CrashHandler*", "vc_redist*", "dxsetup*", "UE*PrereqSetup*", "EasyAntiCheat_Setup*"];

    public IReadOnlyList<Game> Games => games;

    public bool Excluded(string exePath) => excludedRoots.Any(r => Paths.IsUnder(r, exePath));

    public Game? Match(string? exePath, string processName)
    {
        var file = exePath is null ? processName : Path.GetFileName(exePath);
        if (patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, file, ignoreCase: true))) return null;
        if (exePath is null) // elevated or protected: only the exe name is known
            return games.FirstOrDefault(g => g.ExeName is { } e && string.Equals(Path.GetFileNameWithoutExtension(e), Path.GetFileNameWithoutExtension(processName), StringComparison.OrdinalIgnoreCase));
        if (excludedRoots.Any(r => Paths.IsUnder(r, exePath))) return null;
        return games.FirstOrDefault(g => Paths.IsUnder(g.Root, exePath));
    }
}

/// <summary>Steam and Epic libraries plus manual entries, minus exclusions.</summary>
public static class GameCatalog
{
    /// <summary>Steam apps that are not games but live in steamapps\common (00-spec §8).</summary>
    public static readonly Dictionary<string, string> DefaultExcludedAppIds = new()
    {
        ["228980"] = "Steamworks Common Redistributables",
        ["250820"] = "SteamVR",
        ["431960"] = "Wallpaper Engine",
        ["993090"] = "Lossless Scaling",
        ["365670"] = "Blender",
        ["1905180"] = "OBS Studio",
        ["1283970"] = "YoloMouse",
        ["1711480"] = "Live Waifu Wallpaper",
    };

    public sealed record Result(List<Game> Games, List<Excluded> Excluded);

    public static Result Build(GamesConfig cfg)
    {
        var found = new List<Game>();
        try { found.AddRange(Steam()); } catch (Exception e) { Log.Warn("steam library not read", e); }
        try { found.AddRange(Epic()); } catch (Exception e) { Log.Warn("epic manifests not read", e); }
        found.AddRange(cfg.Manual.Where(m => m.Path.Length > 0).Select(m =>
            new Game(m.Name, "Manual", m.Path, File.Exists(m.Path) ? Path.GetFileName(m.Path) : null)));

        var games = new List<Game>();
        var excluded = new List<Excluded>();
        foreach (var g in found.DistinctBy(g => g.Root.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase))
        {
            if (g.AppId is { } id && DefaultExcludedAppIds.ContainsKey(id) && !cfg.Included.Contains(g.Root, StringComparer.OrdinalIgnoreCase))
                excluded.Add(new(g, true));
            else if (cfg.Excluded.Contains(g.Root, StringComparer.OrdinalIgnoreCase)) excluded.Add(new(g, false));
            else games.Add(g);
        }
        return new(games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), excluded);
    }

    public static GameMatcher Matcher(Result r, GamesConfig cfg) => new(
        r.Games.Where(g => !cfg.Off.Contains(g.Root, StringComparer.OrdinalIgnoreCase)).ToList(),
        r.Excluded.Select(x => x.Game.Root).ToList(),
        GameMatcher.DefaultPatterns);

    static IEnumerable<Game> Steam()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) yield break;
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var lib in SteamLibraryParser.Libraries(File.ReadAllText(vdf)))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                (string AppId, string Name, string InstallDir)? m = null;
                try { m = SteamLibraryParser.Manifest(File.ReadAllText(acf)); } catch (IOException) { }
                if (m is not { } a) continue;
                var root = Path.GetFullPath(Path.Combine(apps, "common", a.InstallDir));
                if (Directory.Exists(root)) yield return new(a.Name, "Steam", root, AppId: a.AppId);
            }
        }
    }

    static IEnumerable<Game> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            Game? g = null;
            try { g = EpicManifestParser.Parse(File.ReadAllText(f)); } catch (Exception e) when (e is IOException or JsonException) { }
            if (g is not null && Directory.Exists(g.Root)) yield return g with { Root = Path.GetFullPath(g.Root) };
        }
    }
}

/// <summary>Polls the process list every 2 s. Paths are cached by pid; a pid that disappears is forgotten.</summary>
public sealed class GameWatcher
{
    // ponytail: cache key is the pid alone; a pid reused within one 2 s poll keeps the old answer. Add the start time if that ever bites.
    readonly Dictionary<int, Game?> seen = [];

    public void Reset() => seen.Clear();

    public List<(int Pid, Game Game)> Poll(GameMatcher m)
    {
        var alive = new HashSet<int>();
        var result = new List<(int, Game)>();
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                alive.Add(p.Id);
                if (!seen.TryGetValue(p.Id, out var g))
                    seen[p.Id] = g = p.Id <= 4 ? null : m.Match(ProcessInfo.ExePath(p.Id), p.ProcessName);
                if (g is not null) result.Add((p.Id, g));
            }
        foreach (var pid in seen.Keys.Where(k => !alive.Contains(k)).ToList()) seen.Remove(pid);
        return result;
    }
}
