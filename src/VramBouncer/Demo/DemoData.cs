using VramBouncer.Features;
using VramBouncer.Shell;

namespace VramBouncer.Demo;

/// <summary>
/// --demo: believable data for screenshots and GIFs. Names like Alex, example.com, C:\Users\demo\…; never real data.
/// Under --demo no real services start, nothing is written, no hotkeys, no update check (00-shared §2.3).
/// The Overview shows a game in the dark theme and an idle PC in the light one (spec §10: overview-game-dark, overview-idle-light).
/// </summary>
public static class DemoData
{
    const long GB = 1L << 30;
    static long G(double gb) => (long)(gb * GB);

    public static readonly DateTime Now = DateTime.UtcNow;
    public const string LlamaCmd = @"C:\Users\demo\llm\llama.cpp\llama-server.exe -m C:\Users\demo\llm\models\Qwen3.8-27B-IQ3_XXS.gguf --port 8080 -c 32768 -fa on --fit on";

    public static Snap Llama => new()
    {
        Backend = "llama-server", Model = "Qwen3.8-27B-IQ3_XXS", Port = 8080, VramBytes = G(12.9), Restore = true,
        Exe = @"C:\Users\demo\llm\llama.cpp\llama-server.exe",
    };
    public static Snap Qwen => new() { Backend = "ollama", Model = "qwen3:1.7b", ContextLength = 8192, KeepAlive = -1, VramBytes = G(1.4), Restore = true };
    public static Snap Comfy => new() { Backend = "comfyui", Model = "ComfyUI", VramBytes = G(0.4) };
    public static Snap MLink => new() { Backend = "command", Model = "MLink", Restore = true };

    public static GpuMemory Gpu(double used) => new("NVIDIA GeForce RTX 5060 Ti", 16 * GB, G(used), 16 * GB - G(used));

    public static Dictionary<string, Probe> Loaded(bool llama = true, bool qwen = true, bool comfy = true) => new()
    {
        ["llama-server"] = llama ? new(true, "ok", [Llama], [], LlamaCmd) : Probe.None(),
        ["ollama"] = new(true, "0.40.6", qwen ? [Qwen, new() { Backend = "ollama", Model = "nomic-embed-text", VramBytes = G(0.3) }] : [], []),
        ["comfyui"] = new(true, "0.42.1", comfy ? [Comfy] : [Comfy with { VramBytes = 0 }], []),
        ["lmstudio"] = Probe.None(),
    };

    public static View Idle() => new()
    {
        Phase = Phase.Idle, Gpu = Gpu(15.2), Probes = Loaded(), Catalog = Catalog(),
        Hints = [new(@"C:\Users\demo\Games\RetroArch\retroarch.exe", G(2.1))],
        LastEvent = $"{DateTime.Now.AddMinutes(-37):HH:mm} " + string.Format(Loc.T("Notify.Restored"), 18),
    };

    public static View InGame(DateTime since) => new()
    {
        Phase = Phase.InGame, Game = "Dota 2", Since = since, Gpu = Gpu(6.9), GameBytes = G(5.8), Catalog = Catalog(),
        Probes = Loaded(llama: false, qwen: false, comfy: false), Evicted = [Llama, Qwen, Comfy],
        LastEvent = $"{DateTime.Now:HH:mm} Dota 2 → {Loc.T("Event.Evicted")}: Qwen3.8-27B-IQ3_XXS, qwen3:1.7b, ComfyUI",
    };

    static GameCatalog.Result? catalog;
    public static GameCatalog.Result Catalog() => catalog ??= new(
    [
        new("Alan Wake 2", "Epic", @"C:\Program Files\Epic Games\AlanWake2", "AlanWake2.exe"),
        new("Baldur's Gate 3", "Steam", @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3", AppId: "1086940"),
        new("Counter-Strike 2", "Steam", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", AppId: "730"),
        new("Cyberpunk 2077", "Steam", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", AppId: "1091500"),
        new("Dota 2", "Steam", @"D:\SteamLibrary\steamapps\common\dota 2 beta", AppId: "570"),
        new("Fortnite", "Epic", @"C:\Program Files\Epic Games\Fortnite", "FortniteLauncher.exe"),
        new(Loc.T("Demo.Emulator"), "Manual", @"C:\Users\demo\Games\Emulator\emu.exe", "emu.exe"),
    ],
    [
        new(new("Wallpaper Engine", "Steam", @"D:\SteamLibrary\steamapps\common\wallpaper_engine", AppId: "431960"), true),
        new(new("Lossless Scaling", "Steam", @"D:\SteamLibrary\steamapps\common\Lossless Scaling", AppId: "993090"), true),
    ]);

    public static void Fill()
    {
        var s = Settings.Current; // in memory only under --demo
        s.Backends.Commands.Add(new()
        {
            Name = "MLink",
            Evict = @"C:\Users\demo\MLink\.venv\Scripts\pythonw.exe -m mlink_v2.llama off --hold",
            Restore = @"C:\Users\demo\MLink\.venv\Scripts\pythonw.exe -m mlink_v2.llama restore",
            Check = "curl.exe -sf http://127.0.0.1:8090/health",
        });
        s.Games.Off.Add(@"C:\Program Files\Epic Games\Fortnite");
        s.Games.Budgets[@"D:\SteamLibrary\steamapps\common\dota 2 beta"] = 6;
        s.Games.Budgets[@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"] = 8;
        var game = Args.Current.Page is null or "overview" && Args.Current.Theme != "light" && !Args.Current.Autoplay;
        Vm.I.Apply(game ? InGame(DateTime.UtcNow.AddSeconds(-42)) : Idle());
        ModelsPage.LastPlan =
        [
            string.Format(Loc.T("Plan.Kill"), "llama-server", "Qwen3.8-27B-IQ3_XXS", Engine.Gb(G(12.9))),
            string.Format(Loc.T("Plan.Unload"), "Ollama", "qwen3:1.7b", Engine.Gb(G(1.4))),
            string.Format(Loc.T("Plan.Unload"), "Ollama", "nomic-embed-text", Engine.Gb(G(0.3))) + " · " + Loc.T("Plan.NoRestore"),
            string.Format(Loc.T("Plan.Unload"), "ComfyUI", "ComfyUI", Engine.Gb(G(0.4))) + " · " + Loc.T("Plan.NoRestore"),
            string.Format(Loc.T("Plan.Command"), "MLink"),
        ];
        Vm.I.LoadHistory(History());
    }

    /// <summary>12 sessions over a week.</summary>
    static IEnumerable<Session> History()
    {
        var day = DateTime.Today.ToUniversalTime();
        (string Game, int DaysAgo, double Hour, int Minutes, double Sec, bool Guard)[] rows =
        [
            ("Dota 2", 0, 19.2, 42, 18, false), ("Dota 2", 0, 14.5, 38, 17, false), ("Counter-Strike 2", 1, 21.1, 55, 19, true),
            ("Baldur's Gate 3", 1, 16.0, 124, 21, false), ("Dota 2", 2, 20.3, 47, 18, false), ("Cyberpunk 2077", 3, 22.0, 96, 24, false),
            ("Dota 2", 3, 18.4, 35, 17, false), ("Alan Wake 2", 4, 21.5, 71, 22, true), ("Dota 2", 5, 19.0, 51, 18, false),
            (Loc.T("Demo.Emulator"), 5, 13.2, 28, 15, false), ("Counter-Strike 2", 6, 20.7, 63, 19, false), ("Dota 2", 6, 17.1, 40, 18, false),
        ];
        foreach (var r in rows)
        {
            var start = day.AddDays(-r.DaysAgo).AddHours(r.Hour - 5);
            yield return new Session
            {
                Game = r.Game, Start = start, End = start.AddMinutes(r.Minutes), RestoreSec = r.Sec, Result = "ok",
                Evicted = [new("llama-server", "Qwen3.8-27B-IQ3_XXS", G(12.9)), new("ollama", "qwen3:1.7b", G(1.4)), new("comfyui", "ComfyUI", G(0.4))],
                Guard = r.Guard ? [string.Format(Loc.T("Guard.Reloaded"), "qwen3:1.7b")] : [],
            };
        }
    }
}
