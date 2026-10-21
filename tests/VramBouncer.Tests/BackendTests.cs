using VramBouncer.Features;
using VramBouncer.Shell;

namespace VramBouncer.Tests;

[TestClass]
public class BackendTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 21, 19, 0, 0, TimeSpan.FromHours(5));

    [TestMethod]
    public void OllamaRestorePolicyByExpiry()
    {
        // keep_alive -1 shows up in /api/ps as an expires_at centuries ahead
        Assert.AreEqual((true, (long?)-1), OllamaRestorePolicy.Decide(Now.AddYears(290), Now, RestorePolicy.LongLived));
        Assert.AreEqual((true, (long?)7200), OllamaRestorePolicy.Decide(Now.AddHours(2), Now, RestorePolicy.LongLived));
        Assert.AreEqual((false, (long?)null), OllamaRestorePolicy.Decide(Now.AddMinutes(29), Now, RestorePolicy.LongLived));
        Assert.AreEqual((true, (long?)null), OllamaRestorePolicy.Decide(Now.AddMinutes(4), Now, RestorePolicy.Always));
        Assert.AreEqual((false, (long?)null), OllamaRestorePolicy.Decide(Now.AddYears(290), Now, RestorePolicy.Never));
    }

    [TestMethod]
    public void OllamaHostLikeOllamaDoes()
    {
        Assert.AreEqual("http://127.0.0.1:11434", Http.OllamaDefault(null));
        Assert.AreEqual("http://127.0.0.1:11434", Http.OllamaDefault("0.0.0.0"));
        Assert.AreEqual("http://127.0.0.1:12000", Http.OllamaDefault("0.0.0.0:12000"));
        Assert.AreEqual("http://localhost:11434", Http.OllamaDefault("http://localhost:11434/"));
        Assert.AreEqual("http://localhost:80", Http.OllamaDefault("http://localhost"));
        Assert.AreEqual("http://[::1]:11434", Http.OllamaDefault("[::1]:11434"));
        Assert.AreEqual("http://127.0.0.1:11434", Http.OllamaDefault(":11434"));
    }

    [TestMethod]
    public void OnlyLoopbackAddresses()
    {
        Assert.AreEqual("http://127.0.0.1:8188", Http.Loopback("http://127.0.0.1:8188/"));
        Assert.AreEqual("http://localhost:11434", Http.Loopback("http://localhost:11434"));
        Assert.IsNull(Http.Loopback("http://192.168.1.5:11434"));
        Assert.IsNull(Http.Loopback("http://example.com"));
        Assert.IsNull(Http.Loopback("not a url"));
    }

    [TestMethod]
    public void LlamaArgsPortAndModel()
    {
        Assert.AreEqual(8081, LlamaArgs.Port(CmdLine.Args("-m \"D:\\m\\a b.gguf\" --port 8081 -ngl 99")));
        Assert.AreEqual(8082, LlamaArgs.Port(CmdLine.Args("--port=8082")));
        Assert.AreEqual(8080, LlamaArgs.Port(CmdLine.Args("-m x.gguf --embedding")));
        Assert.AreEqual(@"D:\m\a b.gguf", LlamaArgs.Model(CmdLine.Args("-m \"D:\\m\\a b.gguf\" --port 8081")));
    }

    [TestMethod]
    public void LlamaArgsRelativePaths()
    {
        var args = CmdLine.Args(@"-m models\q.gguf --mmproj D:\m\p.gguf --api-key-file key.txt --chat-template-file=t.jinja --log-file C:\l.log -c 8192");
        CollectionAssert.AreEqual(new[] { @"models\q.gguf", "key.txt", "t.jinja" }, LlamaArgs.RelativePaths(args));
    }

    [TestMethod]
    public void LogFileIsAddedOnce()
    {
        var once = LlamaArgs.WithLogFile("-m \"D:\\a b.gguf\" --port 8081", @"C:\Users\demo\logs\llama-server.log");
        Assert.AreEqual(@"C:\Users\demo\logs\llama-server.log", CmdLine.Args(once)[^1]);
        Assert.AreEqual(once, LlamaArgs.WithLogFile(once, @"C:\other.log"));
        Assert.AreEqual("--log-file=x.log", LlamaArgs.WithLogFile("--log-file=x.log", @"C:\other.log"));
        Assert.HasCount(2, CmdLine.Args(LlamaArgs.WithLogFile("", @"C:\l.log")));
    }

    [TestMethod]
    public void KillLoopWithin60Seconds()
    {
        var killed = new DateTime(2026, 10, 21, 14, 0, 0, DateTimeKind.Utc);
        Assert.IsTrue(KillLoop.IsWatchdog(killed, killed.AddSeconds(59)));
        Assert.IsFalse(KillLoop.IsWatchdog(killed, killed.AddSeconds(61)));
        Assert.IsFalse(KillLoop.IsWatchdog(killed, killed.AddSeconds(-5))); // the old process, started before the kill
    }
}

[TestClass]
public class OllamaParseTests
{
    [TestMethod]
    public void ApiPsGivesSnapshots()
    {
        var now = new DateTimeOffset(2026, 10, 21, 19, 0, 0, TimeSpan.FromHours(5));
        var ps = System.Text.Json.Nodes.JsonNode.Parse("""
            {"models":[
              {"name":"qwen3:8b","model":"qwen3:8b","size":6600000000,"size_vram":6100000000,"context_length":8192,
               "expires_at":"2318-02-20T12:47:05.123456789+05:00"},
              {"name":"gemma3:4b","size":4000000000,"size_vram":4000000000,"context_length":4096,
               "expires_at":"2026-10-21T19:04:59.5+05:00"},
              {"name":"odd","size":1,"size_vram":1,"expires_at":"not a date"}]}
            """);
        var s = OllamaBackend.Parse(ps, now, RestorePolicy.LongLived);
        Assert.AreEqual(("qwen3:8b", 8192, 6100000000L, true, (long?)-1), (s[0].Model, s[0].ContextLength, s[0].VramBytes, s[0].Restore, s[0].KeepAlive));
        Assert.IsFalse(s[1].Restore); // 5 minutes left: Ollama loads it on the next request
        Assert.IsFalse(s[2].Restore);
    }
}
