using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using STS2Mobile.Patches;

internal static class Program
{
    private static int Main()
    {
        foreach (Action<object> ready in new Action<object>[] {
            CombatBackgroundPatches.CombatRoomReadyPostfix,
            EventLayoutPatches.ReadyPostfix,
            MobileLayoutPatches.MainMenuReadyPostfix })
        {
            var owner = CreateDetachedOwner(ready);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(!owner.IsAlive, ready.Method.Name + " retains a departed scene without another scale change");
            CheckReentryAndRepeatedReady(ready);
        }
        CheckMainMenuRoundTrip();
        CheckParticleBounds();
        CheckLearnedSnapshot();
        Console.WriteLine("Resource safety regressions passed: departed owners collectible; reentry/duplicate Ready; ambient-only particle bounds; scoped bounded atomic learned snapshots.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedOwner(Action<object> ready)
    {
        var owner = new Control();
        if (ready == MobileLayoutPatches.MainMenuReadyPostfix)
        {
            var background = new Control();
            background.NamedNodes["%Logo"] = new Node2D();
            owner.NamedNodes["%MainMenuBg"] = background;
            owner.NamedNodes["%MainMenuTextButtons"] = new Control();
            SelectScale(110, 1527);
        }
        owner.Enter();
        ready(owner);
        owner.Exit();
        SelectScale(100, 1680);
        return new WeakReference(owner);
    }

    private static void CheckReentryAndRepeatedReady(Action<object> ready)
    {
        var owner = new Control();
        owner.Enter();
        ready(owner);
        int before = owner.TreeReads;
        UiScalePatches.ApplyUiScale();
        int expected = owner.TreeReads - before;
        Check(expected > 0, "active layout did not react to scale change");
        owner.Exit();
        before = owner.TreeReads;
        UiScalePatches.ApplyUiScale();
        Check(owner.TreeReads == before, "detached layout still received scale change");
        owner.Enter();
        UiScalePatches.ApplyUiScale();
        Check(owner.TreeReads - before == expected, "reentered layout lost its scale subscription");
        ready(owner);
        before = owner.TreeReads;
        UiScalePatches.ApplyUiScale();
        Check(owner.TreeReads - before == expected, "repeated Ready duplicated subscription");
        owner.Exit();
    }

    private sealed class ScaleItem { public Vector2I resolution; }
    private sealed class ScaleDropdown
    {
        public GodotObject _currentOptionLabel = new();
        public void CloseDropdown() { }
    }

    private static void SelectScale(int percent, int width)
    {
        Engine.Tree.Root.ContentScaleSize = new Vector2I(width, 1080);
        UiScalePatches.ScaleItemSelectedPrefix(new ScaleDropdown(), new ScaleItem { resolution = new Vector2I(percent, 0) });
        UiScalePatches.ApplyUiScale();
    }

    private static object ButtonGeometry(Control buttons) => (
        buttons.AnchorLeft, buttons.AnchorRight, buttons.AnchorTop, buttons.AnchorBottom,
        buttons.OffsetLeft, buttons.OffsetRight, buttons.OffsetTop, buttons.OffsetBottom,
        buttons.GrowHorizontal, buttons.GrowVertical, buttons.SizeFlagsHorizontal, buttons.SizeFlagsVertical);

    private static void CheckMainMenuRoundTrip()
    {
        var menu = new Control();
        var buttons = new Control
        {
            AnchorLeft = 0.15f, AnchorRight = 0.75f, AnchorTop = 0.12f, AnchorBottom = 0.82f,
            OffsetLeft = 12, OffsetRight = -25, OffsetTop = 18, OffsetBottom = -36,
            GrowHorizontal = Control.GrowDirection.Begin, GrowVertical = Control.GrowDirection.End,
            SizeFlagsHorizontal = Control.SizeFlags.Fill, SizeFlagsVertical = Control.SizeFlags.Expand,
        };
        var logo = new Node2D { Position = new Vector2(128, 75) };
        var background = new Control();
        menu.NamedNodes["%MainMenuTextButtons"] = buttons;
        menu.NamedNodes["%MainMenuBg"] = background;
        background.NamedNodes["%Logo"] = logo;
        object neutralButtons = ButtonGeometry(buttons);
        Vector2 neutralLogo = logo.Position;
        menu.Enter();
        MobileLayoutPatches.MainMenuReadyPostfix(menu);
        foreach (var (scale, width) in new[] { (100, 1680), (110, 1527), (120, 1400), (110, 1527), (100, 1680) })
        {
            SelectScale(scale, width);
            Check(logo.Position.X == neutralLogo.X + (scale > 100 ? width * 0.25f : 0) && logo.Position.Y == neutralLogo.Y,
                "menu logo drifted from its neutral position");
            object once = ButtonGeometry(buttons);
            Vector2 logoOnce = logo.Position;
            UiScalePatches.ApplyUiScale();
            MobileLayoutPatches.MainMenuReadyPostfix(menu);
            Check(once.Equals(ButtonGeometry(buttons)) && logoOnce.Equals(logo.Position), "menu reapply changed geometry");
            if (scale == 100)
                Check(neutralButtons.Equals(ButtonGeometry(buttons)), "menu did not restore anchors/offsets/grow/size flags");
        }
        menu.Exit();
    }

    private static void CheckParticleBounds()
    {
        var root = new Node { SceneFilePath = "res://scenes/backgrounds/waterfall_giant_boss/layers/waterfall_giant_boss_bg_00_a.tscn" };
        var fog = new GpuParticles2D { Preprocess = 101, Lifetime = 5 };
        var pulse = new GpuParticles2D { Preprocess = 100, Lifetime = 5, Explosiveness = 0.95f };
        var oneShot = new GpuParticles2D { Preprocess = 100, Lifetime = 5, OneShot = true };
        var shader = new GpuParticles2D { Preprocess = 100, Lifetime = 5, ProcessMaterial = new Material() };
        var stars = new GpuParticles2D { Preprocess = 30, Lifetime = 30 };
        var unrelated = new GpuParticles2D { Preprocess = 100, Lifetime = 5 };
        foreach (var particle in new[] { fog, pulse, oneShot, shader, stars })
            root.AddChild(particle);
        var material = fog.ProcessMaterial;
        AndroidParticlePreprocessPatches.BackgroundReadyPostfix(root);
        Check(fog.Preprocess >= fog.Lifetime && fog.Preprocess <= 2 * fog.Lifetime, "ambient warmup must keep a complete emission cycle");
        Check(Math.Abs(fog.Preprocess % fog.Lifetime - 101 % fog.Lifetime) < 0.00001, "emission phase changed");
        Check(fog.Amount == 6 && fog.SpeedScale == 1 && fog.Lifetime == 5 && ReferenceEquals(material, fog.ProcessMaterial), "particle appearance/playback parameters changed");
        Check(pulse.Preprocess == 100 && oneShot.Preprocess == 100 && shader.Preprocess == 100 && stars.Preprocess == 30, "unsupported or long-lived effect was truncated");
        AndroidParticlePreprocessPatches.BackgroundReadyPostfix(unrelated);
        Check(unrelated.Preprocess == 100, "unrelated scene was modified");
        double bounded = fog.Preprocess;
        AndroidParticlePreprocessPatches.BackgroundReadyPostfix(root);
        Check(fog.Preprocess == bounded, "repeated setup changed particle phase");
    }

    private static void CheckLearnedSnapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sts2-resource-safety-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "learned.json");
        try
        {
            string[] paths = Enumerable.Range(0, 520).Select(i => $"res://textures/{i}.png").ToArray();
            LearnedWarmAssetStore.Write(path, "payload-profile-mods-A", paths);
            var same = LearnedWarmAssetStore.Read(path, "payload-profile-mods-A", 512, Accept);
            Check(same.Count == 512 && same.Contains(paths[0]) && !same.Contains(paths[512]), "read did not enforce learned path limit");
            Check(LearnedWarmAssetStore.Read(path, "payload-profile-mods-B", 512, Accept).Count == 0, "stale content context was reused");
            File.WriteAllText(path, "[\"res://old-mod.png\"]");
            Check(LearnedWarmAssetStore.Read(path, "payload-profile-mods-A", 512, Accept).Count == 0, "unscoped legacy list was reused");
            LearnedWarmAssetStore.Write(path, "payload-profile-mods-B", new[] { paths[3], paths[3], "../escape.png" });
            var replaced = LearnedWarmAssetStore.Read(path, "payload-profile-mods-B", 512, Accept);
            Check(replaced.SetEquals(new[] { paths[3] }), "replacement retained stale/invalid/duplicate paths");
            Check(!File.Exists(path + ".tmp"), "atomic snapshot left temporary data behind");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static bool Accept(string path) => path.StartsWith("res://", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal);
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
