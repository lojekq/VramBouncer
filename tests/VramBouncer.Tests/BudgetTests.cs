using VramBouncer.Features;

namespace VramBouncer.Tests;

[TestClass]
public class BudgetTests
{
    const long GB = 1L << 30;
    static Snap S(string name, double gb) => new() { Backend = "ollama", Model = name, VramBytes = (long)(gb * GB) };
    static readonly List<Snap> Loaded = [S("small", 1.4), S("big", 12.9), S("mid", 4.0), new() { Backend = "command", Model = "MLink" }];

    [TestMethod]
    public void NoBudgetEvictsEverything() =>
        Assert.HasCount(4, Budget.Choose(Loaded, 1 * GB, 0));

    [TestMethod]
    public void BiggestFirstUntilTheGameFits()
    {
        // Dota 2 needs 6 GB, 1 GB is free: dropping the 12.9 GB model is enough, the small one and MLink stay
        CollectionAssert.AreEqual(new[] { "big" }, Budget.Choose(Loaded, 1 * GB, 6 * GB).Select(s => s.Model).ToArray());
        Assert.HasCount(0, Budget.Choose(Loaded, 8 * GB, 6 * GB));
    }

    [TestMethod]
    public void UnknownSizesGoOnlyWhenKnownAreNotEnough() =>
        CollectionAssert.AreEqual(new[] { "big", "mid", "small", "MLink" },
            Budget.Choose(Loaded, 0, 20 * GB).Select(s => s.Model).ToArray());

    [TestMethod]
    public void WithoutNvmlEverythingGoes() =>
        Assert.HasCount(4, Budget.Choose(Loaded, null, 6 * GB));
}
