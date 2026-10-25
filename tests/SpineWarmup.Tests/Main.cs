using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;

public partial class Main : Node2D
{
    private const string InvalidationSignal = "_internal_spine_objects_invalidated";
    private Resource _atlas;
    private Resource _skeletonFile;
    private Resource _data;
    private Node2D _source;
    // These are borrowed, sprite-owned native wrappers. Never Dispose them.
    private GodotObject _sourceSkeleton;
    private GodotObject _sourceState;
    private GodotObject _sourceSkin;
    private CanvasItemMaterial _material;
    private Rect2 _sourceBounds;
    private int _sourceStarts;
    private int _assertions;

    public override void _Ready() => Callable.From(() => { _ = RunAsync(); }).CallDeferred();

    private async Task RunAsync()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "Real rendering is required: use Xvfb/OpenGL, not --headless.");
            Require(ClassDB.ClassExists("SpineSprite"), "Load the real spine-godot 4.2 extension before running this project.");
            CreateFixture();
            await DrawFrame();
            _sourceBounds = Bounds(_sourceSkeleton);
            Require(_sourceBounds.Size.IsEqualApprox(new Vector2(30, 18)), "The fixture must have a real 24x24 attachment with native skeleton scale applied.");
            Require(_source.GetChildCount() == 4, "Fixture needs one native mesh plus audio, particles and scripted child.");
            Require(_source.GetChild(0).GetClass() == "SpineMesh2D", "The native runtime must create and own an ordinary mesh child.");
            Require(Engine.Singleton.GetMeta("spine_test_child_entries").AsInt32() == 1
                && Engine.Singleton.GetMeta("spine_test_sprite_entries").AsInt32() == 1, "Fixture scripts must actually execute once.");
            CheckUnavailableSources();
            await CheckIndependentRenderedPreview();
            await CheckRepeatedCreateDestroyAndLateGc();
            await CheckExternalLifetime();
            CheckOriginalUnchanged();
            // Prove the surviving source can still advance its own native track and render.
            _source.Call("update_skeleton", 0.25f);
            Require(Math.Abs(CurrentTime(_sourceState) - 0.375f) < 0.0001f, "The original animation state must remain functional after every preview is disposed.");
            Require(Math.Abs(Bounds(_sourceSkeleton).Position.X - _sourceBounds.Position.X - 20) < 0.001f,
                "Advancing the original must still update its real skeleton geometry.");
            using (var frame = await Capture())
                Require(GreenPixels(frame, 0, 192).Count > 200, "The original mesh must still render after preview destruction and late GC.");
            var audio = _source.GetNode<AudioStreamPlayer>("AutoplayAudio");
            var waveform = audio.Stream;
            audio.Stop();
            audio.Stream = null;
            waveform.Dispose();
            // Let the real audio thread retire its stopped playback before engine shutdown.
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            _source.Free();
            _source = null;
            // Only the custom skin was created by the fixture; state and skeleton stay borrowed.
            _sourceSkin.Dispose();
            _sourceSkin = null;
            _sourceState = null;
            _sourceSkeleton = null;
            ForceGc();
            await DrawFrame();
            _data.Dispose();
            _skeletonFile.Dispose();
            _atlas.Dispose();
            _material.Dispose();
            GD.Print($"PASS: {_assertions} native Spine assertions; independent skeleton/state, real animated pixels, original state isolation, wrapper signals and room/GC lifetime.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception);
            GetTree().Quit(1);
        }
    }

    private void CreateFixture()
    {
        var directory = ProjectSettings.GlobalizePath("user://spine-warmup-fixture");
        Require(DirAccess.MakeDirRecursiveAbsolute(directory) == Error.Ok, "Create fixture directory.");
        using (var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8))
        {
            // Original artwork: an asymmetric white/light-gray checker, generated here.
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                    image.SetPixel(x, y, (x < 3 || y < 2) ? Colors.White : new Color(0.8f, 0.8f, 0.8f));
            Require(image.SavePng(directory + "/tile.png") == Error.Ok, "Save author-created atlas texture.");
        }
        WriteFixture(directory + "/fixture.atlas", FileAccess.GetFileAsString("res://fixture.atlas.txt"));
        WriteFixture(directory + "/fixture.spjson", FileAccess.GetFileAsString("res://fixture.spjson.txt"));
        _atlas = Instantiate<Resource>("SpineAtlasResource");
        _skeletonFile = Instantiate<Resource>("SpineSkeletonFileResource");
        Require(_atlas.Call("load_from_atlas_file", directory + "/fixture.atlas").AsInt32() == (int)Error.Ok, "Native atlas loading must succeed.");
        Require(_skeletonFile.Call("load_from_file", directory + "/fixture.spjson").AsInt32() == (int)Error.Ok, "Native skeleton JSON loading must succeed.");
        _data = Instantiate<Resource>("SpineSkeletonDataResource");
        _data.Call("set_atlas_res", _atlas);
        _data.Call("set_skeleton_file_res", _skeletonFile);
        Require(_data.Call("is_skeleton_data_loaded").AsBool(), "The actual Spine JSON parser must produce loaded skeleton data.");
        _source = Instantiate<Node2D>("SpineSprite");
        _source.Name = "LiveOriginal";
        _source.Position = new Vector2(72, 96);
        _source.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
        _source.Modulate = new Color(0.9f, 0.95f, 0.85f);
        _source.SelfModulate = new Color(0.9f, 0.9f, 1);
        _source.Call("set_update_mode", 2);
        _source.Call("set_skeleton_data_res", _data);
        _source.SetScript(GD.Load<Script>("res://SourceSprite.gd"));
        _material = new CanvasItemMaterial { LightMode = CanvasItemMaterial.LightModeEnum.Unshaded };
        _source.Call("set_normal_material", _material);
        _sourceSkeleton = CallObject(_source, "get_skeleton");
        _sourceState = CallObject(_source, "get_animation_state");
        // A composed skin, not merely a name lookup, catches loss of live/custom skins.
        _sourceSkin = CallObject(_source, "new_skin", "composed-green");
        using (var green = CallObject(_data, "find_skin", "green"))
            _sourceSkin.Call("add_skin", green);
        _sourceSkeleton.Call("set_skin", _sourceSkin);
        _sourceSkeleton.Call("set_slots_to_setup_pose");
        _sourceSkeleton.Call("set_color", new Color(0.8f, 0.9f, 1));
        _sourceSkeleton.Call("set_position", new Vector2(3, 4));
        _sourceSkeleton.Call("set_scale_x", 1.25f);
        _sourceSkeleton.Call("set_scale_y", 0.75f);
        _source.Connect("animation_started", Callable.From<Variant, Variant, Variant>((_, _, _) => _sourceStarts++));
        using (var track = CallObject(_sourceState, "set_animation", "idle", true, 0))
            track.Call("set_track_time", 0.125f);
        var audio = new AudioStreamPlayer
        {
            Name = "AutoplayAudio", Autoplay = true,
            Stream = new AudioStreamWav { Data = new byte[16000], Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = 8000, LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopEnd = 8000 }
        };
        _source.AddChild(audio);
        _source.AddChild(new GpuParticles2D
        {
            Name = "AutoplayParticles", Emitting = true, Visible = false, Amount = 1,
            ProcessMaterial = new ParticleProcessMaterial()
        });
        var scripted = new Node { Name = "ScriptedSideEffect" };
        scripted.SetScript(GD.Load<Script>("res://SideEffect.gd"));
        _source.AddChild(scripted);
        AddChild(_source);
        _source.Call("update_skeleton", 0f);
    }

    private void CheckUnavailableSources()
    {
        Require(SpineWarmupPreview.Create(null) == null, "A missing source has no preview.");
        var empty = Instantiate<Node2D>("SpineSprite");
        try { Require(SpineWarmupPreview.Create(empty) == null, "A source without skeleton data has no preview."); }
        finally { empty.Free(); }
    }

    private async Task CheckIndependentRenderedPreview()
    {
        ForceGc();
        await DrawFrame();
        var dataConnections = Connections(_data, "skeleton_data_changed");
        var dataWrappers = Connections(_data, InvalidationSignal);
        var sourceWrappers = Connections(_source, InvalidationSignal);
        using (var preview = SpineWarmupPreview.Create(_source))
        {
            Require(preview != null, "Loaded source must produce a preview.");
            var node = preview.Node;
            node.Position = new Vector2(248, 96);
            AddChild(node);
            var skeleton = CallObject(node, "get_skeleton");
            var state = CallObject(node, "get_animation_state");
            Require(skeleton.GetInstanceId() != _sourceSkeleton.GetInstanceId()
                && state.GetInstanceId() != _sourceState.GetInstanceId(), "Preview must own independent native skeleton AND animation-state objects.");
            Require(node.GetChildCount() == 1 && node.GetChild(0).GetClass() == "SpineMesh2D", "Only Spine-created native mesh children may exist in the preview.");
            Require(node.GetScript().VariantType == Variant.Type.Nil, "The live source script must not be copied.");
            Require(Engine.Singleton.GetMeta("spine_test_child_entries").AsInt32() == 1
                && Engine.Singleton.GetMeta("spine_test_sprite_entries").AsInt32() == 1, "Preview creation must not run source or descendant scripts.");
            Require(node.Modulate == _source.Modulate && node.SelfModulate == _source.SelfModulate
                && node.TextureFilter == _source.TextureFilter, "Canvas tint and filtering must survive native reconstruction.");
            Require(CallObject(node, "get_normal_material").GetInstanceId() == _material.GetInstanceId(), "The required native rendering material must be retained.");
            Require(skeleton.Call("get_color").AsColor() == _sourceSkeleton.Call("get_color").AsColor()
                && Math.Abs(skeleton.Call("get_scale_x").AsSingle() - 1.25f) < 0.0001f
                && Math.Abs(skeleton.Call("get_scale_y").AsSingle() - 0.75f) < 0.0001f
                && Math.Abs(skeleton.Call("get_x").AsSingle() - 3) < 0.0001f
                && Math.Abs(skeleton.Call("get_y").AsSingle() - 4) < 0.0001f, "Native skeleton tint, position and scales must be preserved.");
            var enumerationConnections = Connections(_data, InvalidationSignal);
            for (var i = 0; i < 64; i++)
                Require(preview.GetAnimationNames().OrderBy(name => name).SequenceEqual(new[] { "hit", "idle" }), "Enumerate exact native animation names.");
            Require(Connections(_data, InvalidationSignal) == enumerationConnections, "Animation enumeration must release temporary native wrappers immediately, before GC.");
            Require(Math.Abs(preview.Play("idle", true) - 1) < 0.0001f, "Return the native idle duration.");
            preview.Sample(0);
            var startBounds = Bounds(skeleton);
            using var startFrame = await Capture();
            var originalPixels = GreenPixels(startFrame, 0, 192);
            var initialPixels = GreenPixels(startFrame, 192, 384);
            Require(originalPixels.Count > 200 && initialPixels.Count > 200, "Both original and composed-skin preview must render actual green textured meshes, not default red skin.");
            Require(Math.Abs(originalPixels.Count - initialPixels.Count) <= 4, "Source and preview need equal rendered attachment area.");
            preview.Sample(0.5f);
            Require(Math.Abs(Bounds(skeleton).Position.X - startBounds.Position.X - 40) < 0.001f,
                "Sampling must move actual native attachment geometry by the animation's expected scaled translation.");
            using var movedFrame = await Capture();
            var movedPixels = GreenPixels(movedFrame, 192, 384);
            Require(Math.Abs(movedPixels.CenterX - initialPixels.CenterX - 40) <= 1, "Rendered preview pixels must move by 40, not merely native wrapper metadata.");
            var stillOriginal = GreenPixels(movedFrame, 0, 192);
            Require(Math.Abs(stillOriginal.CenterX - originalPixels.CenterX) < 0.001f && stillOriginal.Count == originalPixels.Count,
                "Preview sampling must not move or hide the live original's pixels.");
            SaveScreenshot(movedFrame);
            var trackConnections = Connections(node, InvalidationSignal);
            for (var i = 0; i < 64; i++)
            {
                Require(Math.Abs(preview.Play("hit", false) - 0.5f) < 0.0001f, "Return the native hit duration.");
                preview.Sample(0.25f);
                Require(Math.Abs(Bounds(skeleton).Position.X - startBounds.Position.X + 30) < 0.001f, "Hit samples must apply the real opposite translation, without residual idle mixing.");
                preview.Play("idle", true);
                preview.Sample(0.5f);
            }
            Require(Connections(node, InvalidationSignal) == trackConnections, "Repeated Play/Sample must not accumulate track wrappers on the native owner.");
            using (var skin = CallObject(skeleton, "get_skin"))
            {
                skin.Call("remove_attachment", 0, "tile");
                using var previewNamesValue = skin.Call("find_names_for_slot", 0);
                using var originalNamesValue = _sourceSkin.Call("find_names_for_slot", 0);
                using var previewNames = previewNamesValue.AsGodotArray();
                using var originalNames = originalNamesValue.AsGodotArray();
                Require(previewNames.Count == 0 && originalNames.Count == 1 && originalNames[0].AsString() == "tile",
                    "Removing an attachment from the preview's composed skin map must not change the live source skin map.");
            }
            CheckOriginalUnchanged();
        }
        Require(Connections(_data, "skeleton_data_changed") == dataConnections, "Disposal must remove the preview's data-owner signal connection.");
        Require(Connections(_data, InvalidationSignal) == dataWrappers
            && Connections(_source, InvalidationSignal) == sourceWrappers,
            $"Temporary wrapper signal counts changed: data {dataWrappers}->{Connections(_data, InvalidationSignal)}, source {sourceWrappers}->{Connections(_source, InvalidationSignal)}.");
        CheckOriginalUnchanged();
    }

    private async Task CheckRepeatedCreateDestroyAndLateGc()
    {
        // Start from live fixture owners, not pending temporaries from prior assertions.
        ForceGc();
        await DrawFrame();
        var changed = Connections(_data, "skeleton_data_changed");
        var wrappers = Connections(_data, InvalidationSignal);
        for (var i = 0; i < 96; i++)
        {
            using (var preview = SpineWarmupPreview.Create(_source))
            {
                AddChild(preview.Node);
                preview.GetAnimationNames();
                preview.Play((i & 1) == 0 ? "hit" : "idle", false);
                preview.Sample(0.2f);
                Require(preview.Node.GetChildCount() == 1, "Every new preview must retain its own native mesh child.");
            }
            // Alternate delayed and immediate collections to exercise native wrappers after tree destruction.
            if (i % 12 == 11)
            {
                ForceGc();
                await DrawFrame();
                CheckOriginalUnchanged();
                Require(Connections(_data, "skeleton_data_changed") == changed
                    && Connections(_data, InvalidationSignal) == wrappers,
                    $"Owner signals after repetition {i}: data {changed}->{Connections(_data, "skeleton_data_changed")}, wrappers {wrappers}->{Connections(_data, InvalidationSignal)}.");
            }
        }
    }

    private async Task CheckExternalLifetime()
    {
        var room = new Node2D { Name = "TemporaryRoom" };
        AddChild(room);
        using (var preview = SpineWarmupPreview.Create(_source))
        {
            var node = preview.Node;
            room.AddChild(node);
            preview.Play("idle", true);
            preview.Sample(0.5f);
            room.Free();
            Require(!GodotObject.IsInstanceValid(node), "Freeing the room must destroy its preview node.");
            RequireDisposed(() => preview.Sample(0.75f), "Sampling after room destruction must stop at a managed lifetime guard.");
            RequireDisposed(() => preview.Play("hit", false), "Playing after room destruction must stop at a managed lifetime guard.");
        }
        using (var preview = SpineWarmupPreview.Create(_source))
        {
            AddChild(preview.Node);
            preview.Play("idle", true);
            preview.Node.Free();
            RequireDisposed(() => preview.Sample(0.5f), "An externally freed preview must never be sampled through stale native wrappers.");
        }
        var explicitlyDisposed = SpineWarmupPreview.Create(_source);
        AddChild(explicitlyDisposed.Node);
        explicitlyDisposed.Dispose();
        explicitlyDisposed.Dispose();
        RequireDisposed(() => explicitlyDisposed.Sample(0), "Disposed preview rejects sampling, and repeated disposal is safe.");
        ForceGc();
        await DrawFrame();
        CheckOriginalUnchanged();
    }

    private void CheckOriginalUnchanged()
    {
        Require(GodotObject.IsInstanceValid(_source) && GodotObject.IsInstanceValid(_sourceSkeleton)
            && GodotObject.IsInstanceValid(_sourceState), "Preview disposal must leave the live original's borrowed native wrappers valid.");
        Require(Math.Abs(CurrentTime(_sourceState) - 0.125f) < 0.0001f, "Preview work must never advance or replace the original's current track time.");
        using (var current = CallObject(_sourceState, "get_current", 0))
        using (var animation = CallObject(current, "get_animation"))
            Require(animation.Call("get_name").AsString() == "idle", "Preview Play must not change the live original's animation.");
        Require(Bounds(_sourceSkeleton).IsEqualApprox(_sourceBounds), "Preview work must not mutate the original native skeleton geometry.");
        Require(_sourceStarts == 1, "Preview work must not dispatch animation-start signals on the original.");
        Require(_source.GetChildCount() == 4 && _source.GetNode<AudioStreamPlayer>("AutoplayAudio").Playing
            && _source.GetNode<GpuParticles2D>("AutoplayParticles").Emitting, "Original audio, particles and native meshes must remain alive and untouched.");
        GC.KeepAlive(_sourceSkeleton);
        GC.KeepAlive(_sourceState);
    }

    private static void WriteFixture(string path, string text)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null) throw new InvalidOperationException($"Cannot write fixture {path}: {FileAccess.GetOpenError()}");
        file.StoreString(text);
    }

    private async Task DrawFrame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private async Task<Image> Capture()
    {
        await DrawFrame();
        return GetViewport().GetTexture().GetImage();
    }

    private void SaveScreenshot(Image image)
    {
        var path = "user://spine-warmup.png";
        foreach (var argument in OS.GetCmdlineUserArgs())
            if (argument.StartsWith("--screenshot=", StringComparison.Ordinal)) path = argument.Substring("--screenshot=".Length);
        Require(image.SavePng(path) == Error.Ok, "Save the rendered smoke screenshot.");
        GD.Print("Rendered Spine screenshot: " + ProjectSettings.GlobalizePath(path));
    }

    private static (int Count, double CenterX) GreenPixels(Image image, int fromX, int toX)
    {
        var count = 0;
        long sumX = 0;
        for (var y = 0; y < image.GetHeight(); y++)
            for (var x = fromX; x < toX; x++)
            {
                var color = image.GetPixel(x, y);
                if (color.G > 0.25f && color.G > color.R * 2 && color.G > color.B * 2)
                {
                    count++;
                    sumX += x;
                }
            }
        return (count, count == 0 ? double.NaN : (double)sumX / count);
    }
    private static T Instantiate<T>(string className) where T : GodotObject
    {
        using var nativeClass = new StringName(className);
        using var result = ClassDB.Instantiate(nativeClass);
        return (T)result.AsGodotObject();
    }

    private static GodotObject CallObject(GodotObject owner, string methodName, params ReadOnlySpan<Variant> arguments)
    {
        using var method = new StringName(methodName);
        using var result = owner.Call(method, arguments);
        GC.KeepAlive(owner);
        return result.AsGodotObject();
    }


    private static Rect2 Bounds(GodotObject skeleton) => skeleton.Call("get_bounds").AsRect2();
    private static int Connections(GodotObject owner, string signal)
    {
        using var connections = (Godot.Collections.Array)owner.GetSignalConnectionList(signal);
        return connections.Count;
    }
    private static float CurrentTime(GodotObject state)
    {
        using var track = CallObject(state, "get_current", 0);
        return track.Call("get_track_time").AsSingle();
    }
    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
    private void RequireDisposed(Action action, string message)
    {
        try { action(); }
        catch (ObjectDisposedException) { _assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private void Require(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
