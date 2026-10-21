using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public sealed class LmStudioConfig
{
    public bool On { get; set; } = true;
    public string Url { get; set; } = "http://127.0.0.1:1234";
    public string Restore { get; set; } = RestorePolicy.Always;
    /// <summary>API token, DPAPI (CurrentUser) protected, base64. Only needed when "Require Authentication" is on in LM Studio.</summary>
    public string? Token { get; set; }

    public static string? Protect(string? token) => string.IsNullOrEmpty(token) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public string? PlainToken()
    {
        try { return Token is null ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Token), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; } // another user or PC: ask again
    }
}

/// <summary>
/// LM Studio 0.4+ REST API v1: GET /api/v1/models (loaded_instances[].id and .config), POST /api/v1/models/unload {instance_id},
/// POST /api/v1/models/load {model, context_length, …}. With the HTTP server off, `lms unload --all` still unloads (nothing to restore then).
/// </summary>
public sealed class LmStudioBackend(LmStudioConfig cfg) : Backend
{
    /// <summary>The load fields LM Studio accepts back (the list also has "parallel", which load does not take).</summary>
    static readonly string[] LoadFields = ["context_length", "eval_batch_size", "flash_attention", "num_experts", "offload_kv_cache_to_gpu"];

    public override string Kind => "lmstudio";
    public override string Title => "LM Studio";
    public override bool On => cfg.On;
    string? Url => Http.Loopback(cfg.Url);
    public static string Lms => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");

    public override async Task<Probe> ProbeAsync(CancellationToken ct)
    {
        if (Url is not { } url) return Probe.None(Loc.T("Models.NotLoopback"));
        if (!Http.Listening(url)) return File.Exists(Lms) ? new(true, null, [], [Loc.T("LmStudio.ServerOff")]) : Probe.None();
        try { return new(true, null, Parse(await Get(url, ct), cfg.Restore), []); }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        { return Probe.None(Loc.T("LmStudio.Token")); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException && !ct.IsCancellationRequested) { return Probe.None(); }
    }

    async Task<JsonNode?> Get(string url, CancellationToken ct)
    {
        using var r = await Send(HttpMethod.Get, url + "/api/v1/models", null, Http.Short, ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    Task<HttpResponseMessage> Send(HttpMethod m, string url, JsonNode? body, TimeSpan timeout, CancellationToken ct) =>
        Http.Send(m, url, body, timeout, ct, cfg.PlainToken());

    /// <summary>models[].loaded_instances[] → snapshots; Args keeps the instance's load config as JSON for the restore.</summary>
    internal static List<Snap> Parse(JsonNode? root, string policy)
    {
        var list = new List<Snap>();
        foreach (var m in root?["models"]?.AsArray() ?? [])
            foreach (var i in m?["loaded_instances"]?.AsArray() ?? [])
            {
                if (i?["id"]?.GetValue<string>() is not { } id) continue;
                var config = new JsonObject();
                if (i["config"] is JsonObject c)
                    foreach (var f in LoadFields)
                        if (c[f] is { } v) config[f] = v.DeepClone();
                list.Add(new()
                {
                    Backend = "lmstudio", Model = m!["key"]?.GetValue<string>() ?? id, Args = config.ToJsonString(),
                    ContextLength = config["context_length"]?.GetValue<int>(), Instance = id,
                    VramBytes = m["size_bytes"]?.GetValue<long>() ?? 0, Restore = policy != RestorePolicy.Never,
                });
            }
        return list;
    }

    public override async Task<Snap> EvictAsync(Snap s, CancellationToken ct)
    {
        if (Url is not { } url || !Http.Listening(url))
        {
            var (code, err) = await CommandRunner.RunAsync($"\"{Lms}\" unload --all", CommandBackend.EvictTimeout, ct);
            if (code != 0) throw new InvalidOperationException(string.Format(Loc.T("Command.Failed"), "lms", code, err));
            return s with { Restore = false };
        }
        using var r = await Send(HttpMethod.Post, url + "/api/v1/models/unload", new JsonObject { ["instance_id"] = s.Instance ?? s.Model }, Http.Short, ct);
        r.EnsureSuccessStatusCode();
        return s;
    }

    public override async Task<string?> RestoreAsync(Snap s, CancellationToken ct)
    {
        var url = Url ?? throw new InvalidOperationException(Loc.T("Models.NotLoopback"));
        if (!Http.Listening(url)) throw new InvalidOperationException(Loc.T("LmStudio.ServerOff"));
        var body = JsonNode.Parse(s.Args ?? "{}") as JsonObject ?? [];
        body["model"] = s.Model;
        using var r = await Send(HttpMethod.Post, url + "/api/v1/models/load", body, Http.Load, ct);
        r.EnsureSuccessStatusCode();
        return null;
    }

    public override async Task<List<string>> GuardAsync(Guard g, CancellationToken ct)
    {
        var events = new List<string>();
        if (Url is not { } url || !Http.Listening(url)) return events;
        List<Snap> loaded;
        try { loaded = Parse(await Get(url, ct), cfg.Restore); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException && !ct.IsCancellationRequested) { return events; }
        foreach (var m in loaded.Where(g.MayEvict))
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
        try { return Parse(await Get(url, ct), cfg.Restore).Any(l => l.Model == s.Model); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException && !ct.IsCancellationRequested) { return null; }
    }
}
