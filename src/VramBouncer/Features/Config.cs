using VramBouncer.Features;

namespace VramBouncer.Shell
{
    // The app's own settings fields: same partial class, same namespace as Shell/Settings.cs
    public sealed partial class Settings
    {
        public GamesConfig Games { get; set; } = new();
        public BackendsConfig Backends { get; set; } = new();
        public TimingsConfig Timings { get; set; } = new();
        public NotifyConfig Notify { get; set; } = new();
        public bool MLinkOffered { get; set; }          // "Use MLink's commands" was offered once
        /// <summary>"Kill llama-server even if I can't bring it back": off, see spec §8.</summary>
        public bool KillWithoutRestore { get; set; }
    }
}

namespace VramBouncer.Features
{
    public sealed class GamesConfig
    {
        public List<ManualGame> Manual { get; set; } = [];
        public List<string> Excluded { get; set; } = [];   // roots marked "Not a game"
        public List<string> Included { get; set; } = [];   // default exclusions the user turned back into games
        public List<string> Off { get; set; } = [];        // roots with "Evict models" off
        public List<string> Known { get; set; } = [];      // roots already announced as "Recognized as a game"
        public Dictionary<string, double> Budgets { get; set; } = []; // root → GB the game needs (v1.0); none = evict all
        public List<string> NotGames { get; set; } = [];   // exe paths answered "Not a game" to the "Is this a game?" hint
    }

    public sealed class ManualGame
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";             // exe or folder
    }

    /// <summary>always | longlived (Ollama: more than 30 min left) | never</summary>
    public static class RestorePolicy
    {
        public const string Always = "always", LongLived = "longlived", Never = "never";
    }

    public sealed class BackendsConfig
    {
        public OllamaConfig Ollama { get; set; } = new();
        public LlamaConfig Llama { get; set; } = new();
        public ComfyConfig Comfy { get; set; } = new();
        public LmStudioConfig LmStudio { get; set; } = new();
        public List<UserCommand> Commands { get; set; } = [];
    }

    public sealed class OllamaConfig
    {
        public bool On { get; set; } = true;
        public string? Url { get; set; }                   // null: OLLAMA_HOST or http://127.0.0.1:11434
        public string Restore { get; set; } = RestorePolicy.LongLived;
    }

    public sealed class LlamaConfig
    {
        public bool On { get; set; } = true;
        public int Port { get; set; } = 8080;
        public string Restore { get; set; } = RestorePolicy.Always;
        public string? Command { get; set; }               // "Edit command": full command line used instead of the captured one
        public bool HandsOff { get; set; }                 // another program restarts it (kill loop seen): never kill again
    }

    public sealed class ComfyConfig
    {
        public bool On { get; set; } = true;
        public string Url { get; set; } = "http://127.0.0.1:8188";
    }

    /// <summary>"My commands": full command lines (exe first). Check exit code 0 = "the model is loaded".</summary>
    public sealed class UserCommand
    {
        public bool On { get; set; } = true;
        public string Name { get; set; } = "";
        public string Evict { get; set; } = "";
        public string Restore { get; set; } = "";
        public string Check { get; set; } = "";
    }

    public sealed class TimingsConfig
    {
        public int GraceSec { get; set; } = 60;
        public double MarginGb { get; set; } = 0.5;
        public int WaitVramMin { get; set; } = 10;
        public int GuardSec { get; set; } = 15;
    }

    public sealed class NotifyConfig
    {
        public bool Evict { get; set; } = true;
        public bool Restore { get; set; } = true;
        public bool Errors { get; set; } = true;
        public bool Summary { get; set; } = true;
    }
}
