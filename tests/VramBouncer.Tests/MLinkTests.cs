using System.IO;
using VramBouncer.Features;
using VramBouncer.Shell;

namespace VramBouncer.Tests;

[TestClass]
public class MLinkTests
{
    const long GB = 1L << 30;

    [TestMethod]
    public void MLinksServerIsKnownByItsKeyAndSlots()
    {
        // The command line MLink's autostart.ps1 builds, shortened
        Assert.IsTrue(MLink.Owns("\"D:\\localllm\\runtime\\llama-b11552-cuda13\\llama-server.exe\" -m \"D:\\m\\q.gguf\" --port 8080 " +
                                 "--api-key-file \"C:\\Users\\demo\\AppData\\Local\\MLinkV2\\llama.key\" --slot-save-path \"C:\\Users\\demo\\AppData\\Local\\MLinkV2\\slots\""));
        Assert.IsFalse(MLink.Owns("\"D:\\llama\\llama-server.exe\" -m D:\\m\\q.gguf --port 8080"));
        Assert.IsFalse(MLink.Owns(null));
    }

    [TestMethod]
    public void CommandsHoldAndRestoreWithoutCheck()
    {
        var c = MLink.Command(@"C:\Users\demo\MLink\MLinkV2\.venv\Scripts\pythonw.exe");
        Assert.IsTrue(MLink.Is(c));
        Assert.AreEqual("", c.Check); // a check would let a watchdog-started model slip through the guard's 15 s
        var (exe, args) = CmdLine.SplitExe(c.Evict);
        Assert.AreEqual(@"C:\Users\demo\MLink\MLinkV2\.venv\Scripts\pythonw.exe", exe);
        CollectionAssert.AreEqual(new[] { "-m", "mlink_v2.llama", "off", "--hold" }, CmdLine.Args(args));
        CollectionAssert.AreEqual(new[] { "-m", "mlink_v2.llama", "restore" }, CmdLine.Args(CmdLine.SplitExe(c.Restore).RawArgs));
        Assert.IsFalse(MLink.Is(new UserCommand { Evict = "wsl.exe -e ollama stop" }));
    }

    [TestMethod]
    public void DetectNeedsTheVenvAndHold()
    {
        var root = Path.Combine(Path.GetTempPath(), "vb-mlink-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsNull(MLink.Detect(root));
            Directory.CreateDirectory(Path.Combine(root, ".venv", "Scripts"));
            Directory.CreateDirectory(Path.Combine(root, "src", "mlink_v2"));
            File.WriteAllText(Path.Combine(root, ".venv", "Scripts", "pythonw.exe"), "");
            File.WriteAllText(Path.Combine(root, "src", "mlink_v2", "llama.py"), "commands.add_parser('off')");
            Assert.IsNull(MLink.Detect(root)); // an MLink from before off --hold
            File.AppendAllText(Path.Combine(root, "src", "mlink_v2", "llama.py"), "\noff.add_argument('--hold')");
            Assert.AreEqual(Path.Combine(root, ".venv", "Scripts", "pythonw.exe"), MLink.Detect(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void RestoreWaitsOnlyForWhatIsStillOut()
    {
        // The owner's case: MLink's watchdog brought the 13.9 GB model back mid-game, 2.3 GB used by the rest:
        // the old rule waited for 14.8 GB free that could never come, 10 minutes every game
        Assert.AreEqual((long)(0.4 * GB) + GB / 2, Bouncer.Needed([((long)(13.9 * GB), true), ((long)(0.4 * GB), false)], GB / 2, 3 * GB));
        // A full card before the game: no more margin than was free then
        Assert.AreEqual(13 * GB + GB / 10, Bouncer.Needed([(13 * GB, false)], GB / 2, GB / 10));
        Assert.AreEqual(13 * GB + GB / 2, Bouncer.Needed([(13 * GB, false)], GB / 2, null)); // after a restart nothing is known
    }
}
