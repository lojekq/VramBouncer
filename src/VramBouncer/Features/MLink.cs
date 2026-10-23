using System.IO;

namespace VramBouncer.Features;

/// <summary>
/// MLink (lojekq/MLink) runs its own llama-server on :8080 with a watchdog that starts it again within 5 minutes.
/// Killing that server is useless; MLink has commands for it: `off --hold` keeps the model off until `restore`.
/// </summary>
public static class MLink
{
    public const int Port = 8080;

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MLink", "MLinkV2");

    /// <summary>pythonw.exe of an MLink that knows `off --hold`, or null.</summary>
    public static string? Detect(string root)
    {
        var py = Path.Combine(root, ".venv", "Scripts", "pythonw.exe");
        var src = Path.Combine(root, "src", "mlink_v2", "llama.py");
        try { return File.Exists(py) && File.Exists(src) && File.ReadAllText(src).Contains("--hold") ? py : null; }
        catch (IOException) { return null; }
    }

    static readonly Lazy<string?> Found = new(() => Detect(Root)); // read once: the view asks every 2 s

    public static string? Detect() => Found.Value;

    /// <summary>MLink starts llama-server with its key and slots in %LOCALAPPDATA%\MLinkV2.</summary>
    public static bool Owns(string? commandLine) => commandLine?.Contains(@"\MLinkV2\", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// No check command on purpose: `off --hold` runs at every game start, so neither MLink's watchdog nor its
    /// self-start on a request can bring the model back mid-game; `restore` only brings back what was running.
    /// </summary>
    public static UserCommand Command(string pythonw) => new()
    {
        Name = "MLink",
        Evict = $"\"{pythonw}\" -m mlink_v2.llama off --hold",
        Restore = $"\"{pythonw}\" -m mlink_v2.llama restore",
    };

    public static bool Is(UserCommand c) => c.Evict.Contains("mlink_v2.llama", StringComparison.OrdinalIgnoreCase);
}
