using System.Windows.Threading;
using VramBouncer.Features;
using VramBouncer.Shell;

namespace VramBouncer.Demo;

/// <summary>
/// --autoplay: the 8 s scenario of spec §10 on a fake clock. Models in place → Dota 2 starts, models leave the bar, toast →
/// 42 minutes later the game closes, the grace counts down → models come back one by one, "Restored in 18 s".
/// </summary>
public static class Autoplay
{
    public static void Start(MainWindow w)
    {
        var real = DateTime.UtcNow;
        var fake = real;
        var speed = 1.0;
        Vm.I.Now = () => fake;
        View idle = DemoData.Idle(), game = idle;

        // Seconds after the window is on screen; gif.ps1 starts recording when --rect-file appears, ffmpeg needs ~1 s
        (double At, Action Do)[] steps =
        [
            (0.0, () => { w.Navigate("overview"); Vm.I.Apply(idle with { LastEvent = null }); }),
            (1.6, () => Vm.I.Apply(idle with { Phase = Phase.Evicting, Game = "Dota 2" })),
            (2.2, () =>
            {
                game = DemoData.InGame(fake) with { LastEvent = $"{DateTime.Now:HH:mm} Dota 2 → {Loc.T("Event.Evicted")}: Qwen3.8-27B-IQ3_XXS, qwen3:1.7b, ComfyUI" };
                Vm.I.Apply(game);
                Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Demo.Toast"), Engine.Gb((long)(14.7 * (1L << 30)))));
            }),
            (3.4, () => { fake = fake.AddMinutes(42); }),                       // fast-forward a match
            (4.4, () =>
            {
                Vm.I.Apply(game with { Phase = Phase.Cooldown, GoneSince = fake, GameBytes = 0, Gpu = DemoData.Gpu(1.1) });
                speed = 40;                                                       // the 60 s grace in 1.5 s
            }),
            (6.0, () => { speed = 1; Vm.I.Apply(Vm.I.View with { Phase = Phase.Restoring }); }),
            (6.5, () => Vm.I.Apply(Vm.I.View with { Evicted = [DemoData.Qwen, DemoData.Comfy], Probes = DemoData.Loaded(qwen: false, comfy: false), Gpu = DemoData.Gpu(14.0) })),
            (7.0, () => Vm.I.Apply(Vm.I.View with { Evicted = [DemoData.Comfy], Probes = DemoData.Loaded(comfy: false), Gpu = DemoData.Gpu(15.4) })),
            (7.3, () =>
            {
                Vm.I.Apply(DemoData.Idle() with { LastEvent = $"{DateTime.Now:HH:mm} " + string.Format(Loc.T("Notify.Restored"), 18) });
                Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Notify.Restored"), 18));
            }),
        ];
        var started = DateTime.UtcNow;
        var last = started;
        var next = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            fake += (now - last) * speed;
            last = now;
            while (next < steps.Length && (now - started).TotalSeconds >= steps[next].At) steps[next++].Do();
            Vm.I.Raise(); // header timer and countdown follow the fake clock
            if (next == steps.Length && (now - started).TotalSeconds > 9) timer.Stop();
        };
        timer.Start();
    }
}
