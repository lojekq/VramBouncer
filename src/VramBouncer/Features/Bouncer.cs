namespace VramBouncer.Features;

public enum Phase { Idle, Evicting, InGame, Cooldown, Restoring, Paused }

public enum Act { Evict, Restore, RestoreLate, Guard }

public enum Cmd { None, Done, Pause, Resume, RestoreNow }

/// <summary>
/// Game = the game being played (or "manual game mode"). GoneSince: no game seen since then (Cooldown, grace counts from it).
/// Before = the phase a pause came from. Skip: "Restore now" during a game, don't evict again until the game is gone.
/// </summary>
public sealed record BState(
    Phase Phase = Phase.Idle, string? Game = null, DateTime? Since = null, DateTime? GoneSince = null,
    DateTime? LastGuard = null, DateTime? PausedUntil = null, Phase Before = Phase.Idle, bool Skip = false);

/// <summary>One step's view of the world. Free is null without NVML; Needed = evicted VRAM + margin.</summary>
public readonly record struct Input(DateTime Now, string? Game, long? Free = null, long Needed = 0, Cmd Cmd = Cmd.None, DateTime? PauseUntil = null);

public sealed record Timings(TimeSpan Grace, TimeSpan WaitVram, TimeSpan Guard)
{
    public static readonly Timings Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
    public static Timings From(TimingsConfig c) => new(TimeSpan.FromSeconds(c.GraceSec), TimeSpan.FromMinutes(c.WaitVramMin), TimeSpan.FromSeconds(c.GuardSec));
}

/// <summary>The state machine as a pure function (spec §5); the engine runs the actions and feeds Done back.</summary>
public static class Bouncer
{
    public static (BState, Act[]) Next(BState s, Input i, Timings t)
    {
        var now = i.Now;
        switch (i.Cmd)
        {
            case Cmd.Pause when s.Phase is not (Phase.Evicting or Phase.Restoring):
                return (s with { Phase = Phase.Paused, PausedUntil = i.PauseUntil, Before = s.Phase == Phase.Paused ? s.Before : s.Phase }, []);
            case Cmd.Resume when s.Phase == Phase.Paused:
                return Resume(s, i, t);
            case Cmd.RestoreNow when s.Phase is Phase.InGame or Phase.Cooldown:
                return (s with { Phase = Phase.Restoring, Skip = i.Game is not null }, [Act.Restore]);
            case Cmd.Done when s.Phase == Phase.Evicting:
                return (s with { Phase = Phase.InGame, LastGuard = now }, []);
            case Cmd.Done when s.Phase == Phase.Restoring:
                return (s with { Phase = Phase.Idle, Game = null, Since = null, GoneSince = null }, []);
        }

        switch (s.Phase)
        {
            case Phase.Idle when i.Game is null:
                return (s.Skip ? s with { Skip = false } : s, []);
            case Phase.Idle when !s.Skip:
                return (s with { Phase = Phase.Evicting, Game = i.Game, Since = now, GoneSince = null }, [Act.Evict]);

            case Phase.InGame when i.Game is null:
                return (s with { Phase = Phase.Cooldown, GoneSince = now }, []);
            case Phase.InGame when s.LastGuard is null || now - s.LastGuard >= t.Guard:
                return (s with { Game = i.Game, LastGuard = now }, [Act.Guard]);
            case Phase.InGame:
                return (s with { Game = i.Game }, []);

            case Phase.Cooldown when i.Game is not null: // another game (or the same one again) before the restore: models stay out
                return (s with { Phase = Phase.InGame, Game = i.Game, GoneSince = null, LastGuard = null }, []);
            case Phase.Cooldown:
                var waitFrom = (s.GoneSince ?? now) + t.Grace;
                if (now < waitFrom) return (s, []);
                if (i.Free is null || i.Free >= i.Needed) return (s with { Phase = Phase.Restoring }, [Act.Restore]);
                if (now - waitFrom >= t.WaitVram) return (s with { Phase = Phase.Restoring }, [Act.RestoreLate]);
                return (s, []);

            case Phase.Paused when s.PausedUntil is { } until && now >= until:
                return Resume(s, i, t);
        }
        return (s, []);
    }

    /// <summary>Back from a pause: recount from what is true now (spec: "снятие паузы → пересчёт").</summary>
    static (BState, Act[]) Resume(BState s, Input i, Timings t)
    {
        var r = s with { PausedUntil = null };
        if (s.Before is Phase.InGame or Phase.Cooldown)
            return i.Game is not null
                ? (r with { Phase = Phase.InGame, Game = i.Game, GoneSince = null, LastGuard = null }, [])
                : (r with { Phase = Phase.Cooldown, GoneSince = s.GoneSince ?? i.Now }, []);
        return Next(r with { Phase = Phase.Idle }, i with { Cmd = Cmd.None }, t);
    }

    /// <summary>
    /// Start-up check of state.json: what is already loaded again (by MLink autostart, by hand) is crossed out;
    /// the rest comes back after the grace if no game runs, otherwise we are in a game.
    /// </summary>
    public static (BState, List<Snap>) Start(List<Snap> evicted, Func<Snap, bool?> loaded, string? game, DateTime now, Timings t)
    {
        var left = evicted.Where(e => loaded(e) != true).ToList();
        if (left.Count == 0) return (new BState(), left);
        return game is not null
            ? (new BState(Phase.InGame, game, now), left)
            : (new BState(Phase.Cooldown, null, now, GoneSince: now - t.Grace), left);
    }
}
