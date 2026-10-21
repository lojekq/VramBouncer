using System.IO;
using System.Text.Json.Nodes;
using VramBouncer.Features;
using VramBouncer.Shell;

namespace VramBouncer.Tests;

[TestClass]
public class V1Tests
{
    [TestMethod]
    public void LmStudioInstancesKeepTheirLoadConfig()
    {
        var root = JsonNode.Parse("""
            {"models":[
              {"type":"llm","key":"google/gemma-4-26b-a4b","size_bytes":15000000000,"loaded_instances":[
                {"id":"google/gemma-4-26b-a4b","config":{"context_length":4096,"eval_batch_size":512,"parallel":4,
                 "flash_attention":true,"num_experts":8,"offload_kv_cache_to_gpu":true}},
                {"id":"google/gemma-4-26b-a4b:2","config":{"context_length":8192}}]},
              {"type":"embedding","key":"nomic","size_bytes":80000000,"loaded_instances":[]}]}
            """);
        var s = LmStudioBackend.Parse(root, RestorePolicy.Always);
        Assert.HasCount(2, s);
        Assert.AreEqual("google/gemma-4-26b-a4b:2", s[1].Instance);
        var config = JsonNode.Parse(s[0].Args!)!.AsObject();
        Assert.AreEqual(4096, config["context_length"]!.GetValue<int>());
        Assert.IsTrue(config["flash_attention"]!.GetValue<bool>());
        Assert.IsNull(config["parallel"]); // load does not take it
        Assert.IsTrue(s[0].Restore);
        Assert.IsFalse(LmStudioBackend.Parse(root, RestorePolicy.Never)[0].Restore);
    }

    [TestMethod]
    public void RouterLoadedModels()
    {
        var data = JsonNode.Parse("""
            [{"id":"a","status":{"value":"loaded","args":["llama-server","-ctx","4096"]}},
             {"id":"b","status":{"value":"unloaded"}},
             {"id":"c","status":{"value":"loading"}},
             {"id":"d","status":{"value":"sleeping"}}]
            """)!.AsArray();
        CollectionAssert.AreEqual(new[] { "a", "c" }, LlamaBackend.Loaded(data));
    }

    [TestMethod]
    public void ApiKeyFromArgsOrFile()
    {
        Assert.AreEqual("k1", LlamaArgs.ApiKey(CmdLine.Args("--port 8080 --api-key k1,k2")));
        var f = Path.Combine(Path.GetTempPath(), "vb-key-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "\n  secret \nother\n");
        try { Assert.AreEqual("secret", LlamaArgs.ApiKey(CmdLine.Args($"--api-key-file \"{f}\""))); }
        finally { File.Delete(f); }
        Assert.IsNull(LlamaArgs.ApiKey(CmdLine.Args("--port 8080")));
    }

    [TestMethod]
    public void HintSkipsKnownExcludedAndServers()
    {
        var m = new GameMatcher([new("Dota 2", "Steam", @"D:\Games\dota 2 beta")], [@"D:\Games\wallpaper_engine"], GameMatcher.DefaultPatterns);
        Assert.IsTrue(HintWatch.Candidate(@"C:\Emu\emu.exe", m, []));
        Assert.IsFalse(HintWatch.Candidate(@"C:\Emu\emu.exe", m, [@"c:\emu\EMU.exe"]));            // answered "Not a game"
        Assert.IsFalse(HintWatch.Candidate(@"D:\Games\dota 2 beta\dota2.exe", m, []));            // already a game
        Assert.IsFalse(HintWatch.Candidate(@"D:\Games\wallpaper_engine\wallpaper64.exe", m, []));  // excluded
        Assert.IsFalse(HintWatch.Candidate(@"D:\llm\llama-server.exe", m, []));                   // a model server
    }
}
