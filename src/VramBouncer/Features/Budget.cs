namespace VramBouncer.Features;

/// <summary>VRAM budget of a game (v1.0): evict only what the game needs, the biggest models first.</summary>
public static class Budget
{
    /// <summary>
    /// Which of the evictable models to unload so that free VRAM reaches the budget. No budget (≤ 0) or no NVML: all of them.
    /// Models of unknown size (my commands) go only if the known ones are not enough.
    /// </summary>
    public static List<Snap> Choose(IReadOnlyList<Snap> loaded, long? free, long budget)
    {
        if (budget <= 0 || free is not { } f) return [.. loaded];
        var pick = new List<Snap>();
        foreach (var s in loaded.Where(s => s.VramBytes > 0).OrderByDescending(s => s.VramBytes).Concat(loaded.Where(s => s.VramBytes <= 0)))
        {
            if (f >= budget) break;
            pick.Add(s);
            f += s.VramBytes;
        }
        return pick;
    }
}
