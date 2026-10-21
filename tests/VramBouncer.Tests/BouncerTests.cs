using VramBouncer.Features;

namespace VramBouncer.Tests;

[TestClass]
public class BouncerTests
{
    const long GB = 1L << 30;
    static readonly Timings T = Timings.Default;
    static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>Fake clock: a step at T0 + seconds.</summary>
    static (BState S, Act[] A) Step(BState s, double sec, string? game, long? free = 16 * GB, long needed = 0, Cmd cmd = Cmd.None, DateTime? until = null) =>
        Bouncer.Next(s, new Input(T0.AddSeconds(sec), game, free, needed, cmd, until), T);

    [TestMethod]
    public void GameStartEvictsThenGuards()
    {
        var (s, a) = Step(new BState(), 0, "Dota 2");
        Assert.AreEqual(Phase.Evicting, s.Phase);
        CollectionAssert.AreEqual(new[] { Act.Evict }, a);
        (s, _) = Step(s, 1, "Dota 2", cmd: Cmd.Done);
        Assert.AreEqual(Phase.InGame, s.Phase);
        Assert.HasCount(0, Step(s, 10, "Dota 2").A);                             // guard every 15 s
        CollectionAssert.AreEqual(new[] { Act.Guard }, Step(s, 16, "Dota 2").A);
        Assert.HasCount(0, Step(new BState(), 0, null).A);                       // no game, nothing to do
    }

    [TestMethod]
    public void SecondGameInCooldownStaysInGameWithoutEviction()
    {
        var s = new BState(Phase.InGame, "Dota 2", T0);
        (s, _) = Step(s, 0, null);
        Assert.AreEqual(Phase.Cooldown, s.Phase);
        var (s2, a) = Step(s, 30, "Counter-Strike 2");
        Assert.AreEqual(Phase.InGame, s2.Phase);
        Assert.AreEqual("Counter-Strike 2", s2.Game);
        Assert.HasCount(0, a);
    }

    [TestMethod]
    public void GraceThenRestoreWaitsForVram()
    {
        var s = new BState(Phase.Cooldown, "Dota 2", T0, GoneSince: T0);
        Assert.HasCount(0, Step(s, 59, null).A);                                         // grace
        Assert.HasCount(0, Step(s, 61, null, free: 10 * GB, needed: 14 * GB).A);         // VRAM still busy
        var (s2, a) = Step(s, 61, null, free: 15 * GB, needed: 14 * GB);
        CollectionAssert.AreEqual(new[] { Act.Restore }, a);
        Assert.AreEqual(Phase.Restoring, s2.Phase);
        CollectionAssert.AreEqual(new[] { Act.Restore }, Step(s, 61, null, free: null, needed: 14 * GB).A); // no NVML: grace only
        (s2, _) = Step(s2, 70, null, cmd: Cmd.Done);
        Assert.AreEqual(Phase.Idle, s2.Phase);
    }

    [TestMethod]
    public void After10MinutesRestoreWithWarning()
    {
        var s = new BState(Phase.Cooldown, "Dota 2", T0, GoneSince: T0);
        Assert.HasCount(0, Step(s, 60 + 599, null, free: 2 * GB, needed: 14 * GB).A);
        CollectionAssert.AreEqual(new[] { Act.RestoreLate }, Step(s, 60 + 600, null, free: 2 * GB, needed: 14 * GB).A);
    }

    [TestMethod]
    public void PausedDoesNothingAndRecountsOnResume()
    {
        var (s, _) = Step(new BState(), 0, null, cmd: Cmd.Pause, until: T0.AddMinutes(15));
        Assert.AreEqual(Phase.Paused, s.Phase);
        Assert.HasCount(0, Step(s, 60, "Dota 2").A);                               // a game starts: no eviction while paused
        var (s2, a) = Step(s, 15 * 60, "Dota 2");                                   // the pause ends by itself
        CollectionAssert.AreEqual(new[] { Act.Evict }, a);
        Assert.AreEqual(Phase.Evicting, s2.Phase);

        var inGame = new BState(Phase.InGame, "Dota 2", T0);
        (s, _) = Step(inGame, 0, "Dota 2", cmd: Cmd.Pause);
        Assert.HasCount(0, Step(s, 600, null).A);                                   // game gone while paused: no restore
        (s2, a) = Step(s, 601, null, cmd: Cmd.Resume);
        Assert.AreEqual(Phase.Cooldown, s2.Phase);
        Assert.HasCount(0, a);
    }

    [TestMethod]
    public void RestoreNowDuringGameDoesNotEvictAgain()
    {
        var (s, a) = Step(new BState(Phase.InGame, "Dota 2", T0), 0, "Dota 2", cmd: Cmd.RestoreNow);
        CollectionAssert.AreEqual(new[] { Act.Restore }, a);
        (s, _) = Step(s, 5, "Dota 2", cmd: Cmd.Done);
        Assert.HasCount(0, Step(s, 7, "Dota 2").A);
        (s, _) = Step(s, 9, null);
        CollectionAssert.AreEqual(new[] { Act.Evict }, Step(s, 11, "Dota 2").A); // next game session: evict as usual
    }

    [TestMethod]
    public void StartCrossesOutWhatIsLoadedAgain()
    {
        List<Snap> evicted = [new() { Backend = "llama-server", Port = 8081 }, new() { Backend = "ollama", Model = "qwen3:8b" }];
        var (s, left) = Bouncer.Start(evicted, e => e.Backend == "llama-server", null, T0, T);
        Assert.AreEqual("qwen3:8b", left.Single().Model);
        Assert.AreEqual(Phase.Cooldown, s.Phase);
        Assert.AreEqual(Phase.Restoring, Step(s, 0, null).S.Phase);                 // no game: back right away, no second grace
        Assert.AreEqual(Phase.InGame, Bouncer.Start(evicted, _ => false, "Dota 2", T0, T).Item1.Phase);
        Assert.AreEqual(Phase.Idle, Bouncer.Start(evicted, _ => true, null, T0, T).Item1.Phase);
    }
}
