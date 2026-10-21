using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public sealed record SavedState(Phase Phase, string? Game, DateTime? Since, List<Snap> Evicted);

/// <summary>One line of history.jsonl: a game session.</summary>
public sealed record Session
{
    public required string Game { get; init; }
    public DateTime Start { get; init; }
    public DateTime? End { get; set; }
    public List<SessionModel> Evicted { get; init; } = [];
    public List<string> Guard { get; init; } = [];
    public double? RestoreSec { get; set; }
    public string Result { get; set; } = "ok";     // ok | warning | error | pending
    public string? Note { get; set; }
}

public sealed record SessionModel(string Backend, string Name, long Bytes);

/// <summary>state.json (atomic, written before any unload) and history.jsonl (90 days). Nothing is written under --demo.</summary>
public static class Store
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // files people may read, not HTML
        Converters = { new JsonStringEnumConverter() },
    };

    public static string StateFile => Path.Combine(AppInfo.DataDir, "state.json");
    public static string HistoryFile => Path.Combine(AppInfo.DataDir, "history.jsonl");

    public static SavedState? Load()
    {
        try { return AtomicFile.Read(StateFile, s => JsonSerializer.Deserialize<SavedState>(s, Json)); }
        catch (IOException e) { Log.Error("state.json not read", e); return null; }
    }

    public static void Save(SavedState s)
    {
        if (Args.Current.Demo) return;
        AtomicFile.WriteAllText(StateFile, JsonSerializer.Serialize(s, Json)); // throws: an unload without a snapshot on disk must not happen
    }

    public static void Append(Session s)
    {
        if (Args.Current.Demo) return;
        try { File.AppendAllText(HistoryFile, JsonSerializer.Serialize(s, Json) + "\n"); }
        catch (IOException e) { Log.Error("history not written", e); }
    }

    /// <summary>Newest first; lines older than 90 days are dropped from the file.</summary>
    public static List<Session> History()
    {
        var list = new List<Session>();
        try
        {
            if (!File.Exists(HistoryFile)) return list;
            var lines = File.ReadAllLines(HistoryFile);
            foreach (var l in lines)
                try { if (JsonSerializer.Deserialize<Session>(l, Json) is { } s) list.Add(s); }
                catch (JsonException) { }
            var keep = list.Where(s => s.Start > DateTime.UtcNow.AddDays(-90)).ToList();
            if (keep.Count < list.Count && !Args.Current.Demo)
                AtomicFile.WriteAllText(HistoryFile, string.Concat(keep.Select(s => JsonSerializer.Serialize(s, Json) + "\n")));
            list = keep;
        }
        catch (IOException e) { Log.Warn("history not read", e); }
        list.Reverse();
        return list;
    }
}
