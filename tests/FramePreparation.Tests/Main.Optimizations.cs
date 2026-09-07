using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using STS2Mobile.Patches;

public partial class Main
{
    private static PackedScene Pack(Node node)
    {
        var packed = new PackedScene();
        Require(packed.Pack(node) == Error.Ok, "Synthetic effect must be a valid native scene.");
        node.Free();
        return packed;
    }

    private async Task CheckVfxReuse()
    {
        var room = new NCombatRoom();
        NCombatRoom.Instance = room;
        AddChild(room);
        using var damageScene = Pack(new NDamageNumVfx());
        using var hitScene = Pack(new NHitSparkVfx());
        NDamageNumVfx.Scene = damageScene;
        NHitSparkVfx.Scene = hitScene;
        using var sharedMaterial = new ParticleProcessMaterial { Color = Colors.White };
        var template = new NShivThrowVfx();
        template.AddParticle(new GpuParticles2D { Name = "Tinted", Emitting = false, ProcessMaterial = sharedMaterial });
        using var shivScene = Pack(template);
        NShivThrowVfx.Scene = shivScene;

        NDamageNumVfx.TestLateRelease = true;
        var first = NDamageNumVfx.Create(Vector2.One, 7);
        room.AddChild(first);
        NDamageNumVfx.TestLateRelease = false;
        var oldLate = first.Late;
        first.Finish.SetResult(true);
        await Frame(); await Frame();
        Require(GodotObject.IsInstanceValid(first) && !first.IsInsideTree(), "A completed effect must become an idle rental.");
        var second = NDamageNumVfx.Create(new Vector2(40, 50), 123);
        Require(ReferenceEquals(first, second) && second.Scale == Vector2.One && second.Modulate == Colors.White,
            "Reused effects must restore visual state before the original factory runs.");
        room.AddChild(second);
        Require(second.Position == new Vector2(40, 50) && second.Text == "123", "Reused effects must still execute the original factory.");
        oldLate.SetResult(true);
        await Frame(); await Frame();
        Require(second.IsInsideTree() && !second.IsQueuedForDeletion(), "A previous playback continuation must not release the next rental.");
        second.Finish.SetResult(true);
        await Frame(); await Frame();

        var burst = new List<NDamageNumVfx>();
        for (int i = 0; i < 24; i++)
        {
            var effect = NDamageNumVfx.Create(Vector2.Zero, i);
            room.AddChild(effect);
            burst.Add(effect);
        }
        Require(burst.All(effect => effect.IsInsideTree()) && burst.Distinct().Count() == 24, "Pool overflow must not drop or alias simultaneous effects.");
        foreach (var effect in burst) effect.Finish.SetResult(true);
        await Frame(); await Frame(); await Frame();
        Require(burst.Count(GodotObject.IsInstanceValid) == 16, "Only the bounded idle set may remain after a burst.");

        var red = NShivThrowVfx.Create(Vector2.Zero, Colors.Red);
        var blue = NShivThrowVfx.Create(Vector2.One, Colors.Blue);
        room.AddChild(red); room.AddChild(blue);
        var redMaterial = (ParticleProcessMaterial)red.GetNode<GpuParticles2D>("Tinted").ProcessMaterial;
        var blueMaterial = (ParticleProcessMaterial)blue.GetNode<GpuParticles2D>("Tinted").ProcessMaterial;
        Require(redMaterial.Color == Colors.Red && blueMaterial.Color == Colors.Blue && sharedMaterial.Color == Colors.White,
            "Simultaneous particles must not tint one another or the scene template.");
        var materials = new Dictionary<ulong, ParticleProcessMaterial> { [red.GetInstanceId()] = redMaterial, [blue.GetInstanceId()] = blueMaterial };
        red.Finish.SetResult(true); blue.Finish.SetResult(true);
        await Frame(); await Frame();
        var green = NShivThrowVfx.Create(Vector2.One * 9, Colors.Green);
        var greenMaterial = (ParticleProcessMaterial)green.GetNode<GpuParticles2D>("Tinted").ProcessMaterial;
        Require(materials.TryGetValue(green.GetInstanceId(), out var prior) && ReferenceEquals(prior, greenMaterial)
            && greenMaterial.Color == Colors.Green, "Each rental must reuse its own material while accepting the new tint.");
        room.AddChild(green);
        green.Finish.SetResult(true);
        await Frame(); await Frame();

        var target = new Node2D { Position = new Vector2(80, 20) };
        room.AddChild(target);
        var hit = NHitSparkVfx.Create(target);
        room.AddChild(hit);
        hit.Finish.SetResult(true);
        await Frame(); await Frame();
        target.Position = new Vector2(100, 40);
        var nextHit = NHitSparkVfx.Create(target);
        room.AddChild(nextHit);
        Require(ReferenceEquals(hit, nextHit) && nextHit.GlobalPosition == target.GlobalPosition, "Hit reuse must rebind its target and replay Ready.");
        // External removal is a cancellation, not a reusable natural completion.
        room.RemoveChild(nextHit);
        nextHit.Finish.SetResult(true);
        await Frame(); await Frame();
        Require(!GodotObject.IsInstanceValid(nextHit), "Detached playback must still be allowed to finish its original cleanup.");

        room.QueueFree();
        await Frame(); await Frame(); await Frame();
        Require(burst.All(effect => !GodotObject.IsInstanceValid(effect)) && !GodotObject.IsInstanceValid(green), "Room exit must release detached idle effects.");

        // Unknown mod lifecycle hooks opt out rather than pooling unreset mod state.
        var foreign = new Harmony("frame-tests.external-vfx-owner");
        var ready = AccessTools.Method(typeof(NHitSparkVfx), "_Ready");
        foreign.Patch(ready, prefix: new HarmonyMethod(typeof(Main), nameof(ForeignReady)));
        try
        {
            var otherRoom = new NCombatRoom();
            NCombatRoom.Instance = otherRoom;
            AddChild(otherRoom);
            var effect = NHitSparkVfx.Create(otherRoom);
            otherRoom.AddChild(effect);
            effect.Finish.SetResult(true);
            await Frame(); await Frame();
            Require(!GodotObject.IsInstanceValid(effect), "A mod-patched lifecycle must retain original allocation and destruction.");
            otherRoom.QueueFree();
            await Frame();
        }
        finally { foreign.Unpatch(ready, HarmonyPatchType.All, foreign.Id); }
        NCombatRoom.Instance = null;
        GD.Print("PASS: VFX lease isolation, bounded retention without dropped effects, reset/tint ownership, cancellation, mod opt-out and room teardown.");
    }

    private static void ForeignReady() { }

    private async Task CheckRuntimeBudgets()
    {
        const string root = "user://runtime-budget";
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(root + "/vfx"));
        var paths = new List<string>();
        var cached = new List<Resource>();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                string path = $"{root}/item-{i}.tres";
                paths.Add(path);
                using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Write)) file.StoreString("[gd_resource type=\"Gradient\" format=3]\n[resource]\n");
                cached.Add(ResourceLoader.Load(path));
            }
            string vfx = root + "/vfx/native.tscn";
            paths.Add(vfx);
            using (var file = FileAccess.Open(vfx, FileAccess.ModeFlags.Write)) file.StoreString("[gd_scene format=3]\n[node name=\"Effect\" type=\"Node2D\"]\n");
            string broken = root + "/broken.tres";
            paths.Add(broken);
            using (var file = FileAccess.Open(broken, FileAccess.ModeFlags.Write)) file.StoreString("[gd_resource broken");
            var session = new AssetLoadingSession(paths);
            for (int i = 0; i < 30; i++) session.Process();
            Require(!session.Completion.IsCompleted && session.Pending > 0 && session.Loaded.Count <= 8,
                "Repeated processing in one frame must defer work without falsely completing the session.");
            for (int frame = 0; frame < 300 && !session.Completion.IsCompleted; frame++)
            {
                await Frame();
                int before = session.Loaded.Count;
                session.Process();
                Require(session.Loaded.Count - before <= 9, "One frame may finalize at most eight regular resources plus the serial VFX result.");
            }
            Require(session.Completion.IsCompleted && session.Pending == 0, "Budgeted queues must drain without starvation.");
            Require(session.Loaded.Count == 33 && session.Loaded[vfx] is PackedScene && session.Errors.SequenceEqual(new[] { broken }),
                "Deferral must preserve successful resources, serial VFX and observable loading errors.");
            foreach (var resource in session.Loaded.Values) resource.Dispose();
            GD.Print("PASS: repeated same-frame budget, eventual complete resources, VFX serialization and native failure path.");
        }
        finally
        {
            foreach (var resource in cached) resource.Dispose();
            foreach (string path in paths) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(root + "/vfx"));
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(root));
        }
    }

    private async Task CheckFontScaling()
    {
        var root = new Control();
        AddChild(root);
        using var theme = new Theme();
        theme.SetFontSize("font_size", "Label", 20);
        var inherited = new Label { Text = "123", Theme = theme };
        var explicitSize = new Label { Text = "456" };
        explicitSize.AddThemeFontSizeOverride("font_size", 26);
        root.AddChild(inherited); root.AddChild(explicitSize);
        AndroidFontSizeScaler.ApplyRecursive(root, 1f);
        Require(!inherited.HasThemeFontSizeOverride("font_size"), "Default scale must preserve theme inheritance.");
        theme.SetFontSize("font_size", "Label", 22);
        AndroidFontSizeScaler.ApplyRecursive(root, 1.5f);
        Require(inherited.GetThemeFontSize("font_size") == 33 && explicitSize.GetThemeFontSize("font_size") == 39,
            "Scaling must use inherited and explicit base sizes, not already-scaled values.");
        await Frame();
        int changed = 0;
        inherited.ThemeChanged += () => changed++;
        AndroidFontSizeScaler.ApplyRecursive(root, 1.5f);
        await Frame();
        Require(changed == 0, "An unchanged scale must not invalidate the theme again.");
        root.RemoveChild(explicitSize); root.AddChild(explicitSize);
        AndroidFontSizeScaler.Apply(explicitSize, 1.5f);
        Require(explicitSize.GetThemeFontSize("font_size") == 39, "Reparenting must not compound the font multiplier.");
        AndroidFontSizeScaler.ApplyRecursive(root, 1f);
        Require(inherited.GetThemeFontSize("font_size") == 22 && explicitSize.GetThemeFontSize("font_size") == 26,
            "Returning to 100% must restore the unscaled sizes.");

        var auto = new MegaLabel();
        var rich = new MegaRichTextLabel();
        auto.AddThemeFontOverride("font", ThemeDB.FallbackFont);
        rich.AddThemeFontOverride("normal_font", ThemeDB.FallbackFont);
        root.AddChild(auto); root.AddChild(rich);
        AndroidFontSizeScaler.ApplyRecursive(root, 2f);
        Require(auto.GetThemeFontSize("font_size") == 40 && rich.GetThemeFontSize("normal_font_size") == 40,
            "Auto-size labels must still apply resized bounds through their original adjust contract.");
        AndroidFontSizeScaler.ApplyRecursive(root, 1f);
        Require(auto.GetThemeFontSize("font_size") == 20 && rich.GetThemeFontSize("normal_font_size") == 20,
            "Auto-size labels must also restore their original bounds.");
        root.QueueFree();
        await Frame();
        GD.Print("PASS: font theme inheritance, non-compounding scale, idempotence, reparent and autosize restoration.");
    }
}
