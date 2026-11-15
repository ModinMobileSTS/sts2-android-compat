using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;

public partial class Main
{
    private const string WarmupProbePrefix = "frame_preparation_render_warmup_";

    private async Task CheckRenderWarmup()
    {
        var viewport = new SubViewport
        {
            Name = "RenderWarmupPixelFixture", Size = new Vector2I(256, 192),
            Disable3D = true, TransparentBg = true, World2D = new World2D(),
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        viewport.AddChild(new Node { Name = "FixtureSentinel" });
        AddChild(viewport);
        try
        {
            await RenderWarmupFrame();
            await CheckRenderWarmupOffTree(viewport);
            CheckRenderWarmupInheritance();
            CheckRenderWarmupProcessMaterial();
            await CheckRenderWarmupDrawingTypes(viewport);
            await CheckRenderWarmupBatches(viewport);
            await CheckRenderWarmupCancellation(viewport);
            await CheckRenderWarmupPins(viewport);
            GD.Print("PASS: native render replicas, off-tree callback isolation, inherited material copies, drawing-type pixels, bounded batching, cancellation, overlay teardown and resource pins.");
        }
        finally { viewport.Free(); }
    }

    private async Task RenderWarmupFrame()
    {
        await Frame();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private static ShaderMaterial RenderWarmupMaterial(int fixtureCase)
    {
        var offset = (fixtureCase * 0.00001f).ToString("0.00000", CultureInfo.InvariantCulture);
        var shader = new Shader
        {
            Code = "shader_type canvas_item; render_mode unshaded; uniform int fixture_case = 0; "
                + "void fragment() { COLOR = vec4(0.12 + UV.x * 0.08 + " + offset
                + " + float(fixture_case) * 0.000001, 0.72, 0.31, 1.0); }"
        };
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("fixture_case", fixtureCase);
        return material;
    }

    private static ImageTexture RenderWarmupWhiteTexture()
    {
        using var image = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return ImageTexture.CreateFromImage(image);
    }

    private static bool IsRenderWarmupPixel(Color color) => color.A > 0.9f
        && color.R > 0.08f && color.R < 0.42f
        && color.G > 0.62f && color.G < 0.82f
        && color.B > 0.23f && color.B < 0.41f;

    private static int RenderWarmupPixelCount(Image image, Rect2 region)
    {
        var count = 0;
        var fromX = Math.Max(0, (int)Math.Floor(region.Position.X));
        var fromY = Math.Max(0, (int)Math.Floor(region.Position.Y));
        var toX = Math.Min(image.GetWidth(), (int)Math.Ceiling(region.End.X));
        var toY = Math.Min(image.GetHeight(), (int)Math.Ceiling(region.End.Y));
        for (var y = fromY; y < toY; y++)
            for (var x = fromX; x < toX; x++)
                if (IsRenderWarmupPixel(image.GetPixel(x, y))) count++;
        return count;
    }

    private async Task RequireRenderWarmupPixels(SubViewport viewport, string drawingType, int frames = 2)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            await RenderWarmupFrame();
            using var image = viewport.GetTexture().GetImage();
            Require(image != null && !image.IsEmpty(), "Render-replica pixel fixtures require a real native rendering backend.");
            if (RenderWarmupPixelCount(image, new Rect2(Vector2.Zero, new Vector2(256, 192))) >= 16) return;
        }
        throw new InvalidOperationException($"The native {drawingType} replica did not draw the distinctive fixture shader into the viewport.");
    }

    private static T RenderWarmupReplica<T>(T source) where T : CanvasItem
    {
        var replica = RenderReplicaWarmup.CreateReplica(source, out var reason);
        Require(replica is T && replica.GetType() == source.GetType(),
            $"Render replicas must preserve {source.GetClass()} rather than substitute another drawing type: {reason}");
        using var script = replica.GetScript();
        Require(script.VariantType == Variant.Type.Nil && !replica.IsInsideTree() && replica.GetParent() == null,
            "A new replica must be an unattached, script-free native node.");
        return (T)replica;
    }

    private static string RenderWarmupProbeScript(string nativeType, string probe) =>
        $"extends {nativeType}\n"
        + $"func _enter_tree():\n\tEngine.set_meta(\"{WarmupProbePrefix}{probe}_enter\", int(Engine.get_meta(\"{WarmupProbePrefix}{probe}_enter\", 0)) + 1)\n"
        + $"func _ready():\n\tEngine.set_meta(\"{WarmupProbePrefix}{probe}_ready\", int(Engine.get_meta(\"{WarmupProbePrefix}{probe}_ready\", 0)) + 1)\n"
        + $"func _process(_delta):\n\tEngine.set_meta(\"{WarmupProbePrefix}{probe}_process\", int(Engine.get_meta(\"{WarmupProbePrefix}{probe}_process\", 0)) + 1)\n";

    private async Task CheckRenderWarmupOffTree(SubViewport viewport)
    {
        var keys = new[] { "root_enter", "root_ready", "root_process", "child_enter", "child_ready", "child_process" };
        foreach (var key in keys) Engine.Singleton.SetMeta(WarmupProbePrefix + key, 0);
        using var rootScript = new GDScript { SourceCode = RenderWarmupProbeScript("Node2D", "root") };
        using var childScript = new GDScript { SourceCode = RenderWarmupProbeScript("ColorRect", "child") };
        Require(rootScript.Reload() == Error.Ok && childScript.Reload() == Error.Ok,
            "Off-tree callback probes must load as real native GDScripts.");
        var template = new Node2D { Name = "ScriptedPackedRoot" };
        template.SetScript(rootScript);
        var child = new ColorRect { Name = "ScriptedChild", Size = new Vector2(20, 20), Material = RenderWarmupMaterial(101) };
        child.SetScript(childScript);
        template.AddChild(child);
        child.Owner = template;
        var audio = new AudioStreamPlayer
        {
            Name = "AutoplayAudio", Autoplay = true,
            Stream = new AudioStreamWav { Data = new byte[64], MixRate = 8000 },
        };
        template.AddChild(audio);
        audio.Owner = template;
        using var packed = Pack(template);
        var source = packed.Instantiate<Node2D>();
        var sourceChild = source.GetNode<ColorRect>("ScriptedChild");
        var sourceAudio = source.GetNode<AudioStreamPlayer>("AutoplayAudio");
        var originalMaterial = sourceChild.Material;
        var originalStream = sourceAudio.Stream;
        var baseline = RenderWarmupHostChildren(viewport);
        var finished = 0;
        sourceAudio.Finished += () => finished++;
        try
        {
            using (var warmup = new RenderReplicaWarmup(viewport, mask: false))
            {
                await warmup.WarmAsync(source);
                Require(warmup.Drawn == 1 && warmup.Failed == 0 && warmup.PostDraws > 0,
                    "A scripted packed scene must contribute its supported static child without running the scene.");
            }
            await RenderWarmupFrame();
            Require(keys.All(key => Engine.Singleton.GetMeta(WarmupProbePrefix + key).AsInt32() == 0),
                "Neither the PackedScene root nor child may enter the tree, run Ready, or process during native warmup.");
            Require(!source.IsInsideTree() && !sourceChild.IsInsideTree() && source.GetParent() == null
                && sourceChild.GetParent() == source && source.GetChildCount() == 2
                && ReferenceEquals(sourceChild.Material, originalMaterial),
                "Off-tree extraction must not reparent or mutate the original scene or child material.");
            using var retainedRootScript = source.GetScript();
            using var retainedChildScript = sourceChild.GetScript();
            Require(retainedRootScript.AsGodotObject() == rootScript && retainedChildScript.AsGodotObject() == childScript,
                "Extraction must retain the scripts attached to the off-tree originals.");
            Require(sourceAudio.Autoplay && !sourceAudio.Playing && ReferenceEquals(sourceAudio.Stream, originalStream) && finished == 0,
                "An off-tree source's autoplay audio must remain idle and must not dispatch playback callbacks.");
            RequireRenderWarmupHostRestored(viewport, baseline);
        }
        finally
        {
            source.Free();
            foreach (var key in keys) Engine.Singleton.RemoveMeta(WarmupProbePrefix + key);
        }
    }

    private static void CheckRenderWarmupInheritance()
    {
        using var shared = Original();
        using var ignored = RenderWarmupMaterial(102);
        var root = new Node2D { Material = shared, Visible = false, Position = new Vector2(91, 37) };
        var inheritedParent = new Node2D { UseParentMaterial = true, Material = ignored };
        var first = new ColorRect { UseParentMaterial = true, Material = ignored, Visible = false, Size = new Vector2(17, 23) };
        var second = new ColorRect { UseParentMaterial = true, Material = ignored, Size = new Vector2(31, 19) };
        root.AddChild(inheritedParent);
        inheritedParent.AddChild(first);
        inheritedParent.AddChild(second);
        var firstReplica = RenderWarmupReplica(first);
        var secondReplica = RenderWarmupReplica(second);
        try
        {
            Require(Replaced(firstReplica) && Replaced(secondReplica)
                && !firstReplica.UseParentMaterial && !secondReplica.UseParentMaterial,
                "Inherited shader compatibility must resolve through CanvasItem parents onto replicas only.");
            var firstCopy = (ShaderMaterial)firstReplica.Material;
            var secondCopy = (ShaderMaterial)secondReplica.Material;
            Require(!ReferenceEquals(firstCopy, shared) && !ReferenceEquals(secondCopy, shared)
                && !ReferenceEquals(firstCopy, secondCopy), "Each inherited replacement must own an isolated material copy.");
            Require(Math.Abs(firstCopy.GetShaderParameter("mix_percentage").AsSingle() - 0.75f) < 0.001f,
                "Inherited replacement parameters must come from the effective source material, not the ignored local material.");
            firstCopy.SetShaderParameter("mix_percentage", 0.125f);
            Require(Math.Abs(secondCopy.GetShaderParameter("mix_percentage").AsSingle() - 0.75f) < 0.001f
                && Math.Abs(shared.GetShaderParameter("mix_percentage").AsSingle() - 0.75f) < 0.001f,
                "Changing one replacement must affect neither another replica nor the source material.");
            Require(ReferenceEquals(root.Material, shared) && shared.Shader.ResourcePath == "res://shaders/dark_blur.gdshader"
                && ReferenceEquals(inheritedParent.Material, ignored) && inheritedParent.UseParentMaterial
                && ReferenceEquals(first.Material, ignored) && first.UseParentMaterial && !first.Visible
                && first.Size == new Vector2(17, 23) && ReferenceEquals(second.Material, ignored) && second.UseParentMaterial
                && !root.Visible && root.Position == new Vector2(91, 37) && !root.IsInsideTree()
                && first.GetParent() == inheritedParent && inheritedParent.GetParent() == root,
                "Resolving inherited materials must leave all original material, inheritance, visibility, layout and ownership state unchanged.");
        }
        finally { firstReplica.Free(); secondReplica.Free(); root.Free(); }

        foreach (Node boundary in new Node[] { new Node(), new CanvasLayer() })
        {
            var outer = new Node2D { Material = ignored };
            var local = new ColorRect { UseParentMaterial = true, Material = shared };
            outer.AddChild(boundary);
            boundary.AddChild(local);
            var copy = RenderWarmupReplica(local);
            try
            {
                Require(Replaced(copy), "A non-CanvasItem boundary must retain the local material, not inherit from another canvas.");
            }
            finally { copy.Free(); outer.Free(); }
        }
        var standalone = new ColorRect { UseParentMaterial = true, Material = shared };
        var standaloneCopy = RenderWarmupReplica(standalone);
        try { Require(Replaced(standaloneCopy), "An orphan using parent material must fall back to its own material."); }
        finally { standaloneCopy.Free(); standalone.Free(); }

        var topParent = new Node2D { Material = ignored };
        var top = new ColorRect { UseParentMaterial = true, TopLevel = true, Material = shared };
        topParent.AddChild(top);
        var topCopy = RenderWarmupReplica(top);
        try { Require(Replaced(topCopy), "A top-level CanvasItem must not inherit a different parent material."); }
        finally { topCopy.Free(); topParent.Free(); }
    }

    private static void CheckRenderWarmupProcessMaterial()
    {
        using var material = new ParticleProcessMaterial();
        var source = new GpuParticles2D { ProcessMaterial = material, Emitting = false };
        // A live script may deliberately restore the 3D settings after the
        // original assignment. A second assignment must not normalize it again.
        material.ParticleFlagDisableZ = false;
        material.Gravity = new Vector3(0, -9.8f, 0);
        var replica = RenderWarmupReplica(source);
        try
        {
            Require(!material.ParticleFlagDisableZ && material.Gravity == new Vector3(0, -9.8f, 0),
                "Creating a GPU replica must not let Godot's setter normalize the live shared process material.");
            var copied = (ParticleProcessMaterial)replica.ProcessMaterial;
            Require(copied.ParticleFlagDisableZ == material.ParticleFlagDisableZ && copied.Gravity == material.Gravity,
                "The replica must compile the original process shape, not the auto-normalized 2D variant.");
        }
        finally { replica.Free(); source.Free(); }
    }

    private async Task CheckRenderWarmupDrawingTypes(SubViewport viewport)
    {
        var texture = RenderWarmupWhiteTexture();
        var process = new ParticleProcessMaterial
        {
            Gravity = Vector3.Zero, InitialVelocityMin = 0, InitialVelocityMax = 0,
            ScaleMin = 1, ScaleMax = 1, Color = Colors.White,
        };
        var mesh = new QuadMesh { Size = new Vector2(24, 24) };
        using var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D, Mesh = mesh, InstanceCount = 3,
        };
        for (var index = 0; index < 3; index++)
            multimesh.SetInstanceTransform2D(index, new Transform2D(0, new Vector2(index * 50, 0)));
        var gpu = new GpuParticles2D
        {
            Name = "Gpu", Texture = texture, ProcessMaterial = process, Emitting = false,
            Amount = 37, Preprocess = 9.25, Lifetime = 4.5, SubEmitter = new NodePath("../SubEmitter"),
        };
        var cpu = new CpuParticles2D
        {
            Name = "Cpu", Texture = texture, Emitting = false, Amount = 29, Preprocess = 7.25,
            Lifetime = 3.5, Gravity = new Vector2(0, 55), InitialVelocityMin = 13, InitialVelocityMax = 17,
        };
        var line = new Line2D
        {
            Name = "Line", Points = new[] { new Vector2(-9, 2), new Vector2(17, 2), new Vector2(17, 21) },
            Width = 7, DefaultColor = Colors.White,
        };
        var group = new CanvasGroup { Name = "Group", FitMargin = 3, ClearMargin = 5 };
        group.AddChild(new ColorRect { Size = new Vector2(24, 24) });
        CanvasItem[] sources =
        {
            gpu, cpu, line,
            new Sprite2D { Name = "Sprite", Texture = texture },
            new ColorRect { Name = "Rect", Size = new Vector2(24, 24) },
            new MeshInstance2D { Name = "Mesh", Mesh = mesh, Texture = texture },
            new MultiMeshInstance2D { Name = "MultiMesh", Multimesh = multimesh, Texture = texture },
            new Polygon2D
            {
                Name = "Polygon", Texture = texture,
                Polygon = new[] { Vector2.Zero, new Vector2(24, 0), new Vector2(24, 24), new Vector2(0, 24) },
            },
            group,
            new TextureRect { Name = "TextureRect", Texture = texture, Size = new Vector2(24, 24) },
            new NinePatchRect { Name = "NinePatch", Texture = texture, Size = new Vector2(24, 24) },
            new Label { Name = "Label", Text = "Native Ag09", Size = new Vector2(128, 64) },
        };
        var live = new Node2D
        {
            Name = "LiveRenderWarmupSources", Visible = false, Position = new Vector2(733, -219),
            Rotation = 0.3f, Scale = new Vector2(1.25f, 0.75f), ProcessMode = ProcessModeEnum.Disabled,
        };
        var liveTransform = live.Transform;
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index].Material = RenderWarmupMaterial(120 + index);
            sources[index].Visible = false;
            sources[index].Modulate = new Color(0.5f, 0.6f, 0.7f, 0.25f);
            live.AddChild(sources[index]);
        }
        var subEmitter = new GpuParticles2D
        {
            Name = "SubEmitter", ProcessMaterial = process, Emitting = false, Amount = 11, Visible = false,
        };
        live.AddChild(subEmitter);
        AddChild(live);
        var sourceActivity = 0;
        foreach (var source in sources)
        {
            source.TreeEntered += () => sourceActivity++;
            source.TreeExiting += () => sourceActivity++;
        }
        var baseline = RenderWarmupHostChildren(viewport);
        try
        {
            await RenderWarmupFrame();
            foreach (var source in sources)
            {
                var material = source.Material;
                var sourcePosition = source is Node2D sourceNode ? sourceNode.Position : ((Control)source).Position;
                var sourceSize = source is Control sourceControl ? sourceControl.Size : Vector2.Zero;
                var childCount = source.GetChildCount();
                var replica = RenderWarmupReplica(source);
                try
                {
                    Require(replica.Visible && !replica.UseParentMaterial,
                        "Hidden originals must produce visible independent native draw items, without changing the originals.");
                    if (replica is GpuParticles2D gpuReplica)
                        Require(gpuReplica.Amount == 2 && gpuReplica.Preprocess == 0 && gpuReplica.Emitting
                            && gpuReplica.SubEmitter.IsEmpty && ReferenceEquals(gpuReplica.ProcessMaterial, process),
                            "GPU replicas must use two particles, no preprocessing and no path to a live sub-emitter.");
                    if (replica is CpuParticles2D cpuReplica)
                        Require(cpuReplica.Emitting && cpuReplica.Amount <= 2 && cpuReplica.Preprocess == 0,
                            "CPU particles must remain native CPU emitters with bounded replica-only playback.");
                    if (replica is Line2D lineReplica)
                        Require(lineReplica.Points.Length >= 2 && lineReplica.Width > 0
                            && lineReplica.Points.Zip(lineReplica.Points.Skip(1), (a, b) => a.DistanceSquaredTo(b)).Any(length => length > 0),
                            "A Line2D replica must contain actual non-degenerate line geometry.");
                    if (replica is MultiMeshInstance2D multiReplica)
                        Require(!ReferenceEquals(multiReplica.Multimesh, multimesh) && multiReplica.Multimesh.InstanceCount > 0,
                            "A MultiMesh replica must own its temporary instance buffer, not mutate the live buffer.");
                    if (replica is Node2D node) node.Position += new Vector2(128, 96);
                    else if (replica is Control control) control.Position = new Vector2(128, 96) - control.Size * 0.5f;
                    viewport.AddChild(replica);
                    if (replica is GpuParticles2D particles)
                    {
                        particles.Restart();
                        particles.RequestParticlesProcess(1.0f / 30.0f);
                    }
                    else if (replica is CpuParticles2D cpuParticles) cpuParticles.Restart();
                    await RequireRenderWarmupPixels(viewport, source.GetClass(), source is GpuParticles2D or CpuParticles2D ? 12 : 2);
                }
                finally { replica.Free(); }
                Require(ReferenceEquals(source.Material, material) && !source.Visible && source.GetParent() == live
                    && source.GetChildCount() == childCount && source.Modulate == new Color(0.5f, 0.6f, 0.7f, 0.25f)
                    && (source is Node2D originalNode ? originalNode.Position == sourcePosition : ((Control)source).Position == sourcePosition)
                    && (source is not Control originalControl || originalControl.Size == sourceSize),
                    "Drawing and freeing a native replica must not change the live source's material, visibility, geometry or parent.");
                await RenderWarmupFrame();
                using var cleared = viewport.GetTexture().GetImage();
                Require(RenderWarmupPixelCount(cleared, new Rect2(Vector2.Zero, new Vector2(256, 192))) == 0,
                    "Freeing a standalone replica must remove its pixels without retaining a hidden drawable clone.");
            }
            using (var warmup = new RenderReplicaWarmup(viewport, mask: false))
            {
                await warmup.WarmAsync(live);
                Require(warmup.Drawn >= sources.Length && warmup.Failed == 0 && warmup.PeakBatch <= 6,
                    "Live hidden sources must also complete native batched submission without restarting their own emitters.");
            }
            await RenderWarmupFrame();
            Require(!gpu.Emitting && !gpu.Visible && gpu.Amount == 37 && gpu.Preprocess == 9.25 && gpu.Lifetime == 4.5
                && gpu.SubEmitter == new NodePath("../SubEmitter") && ReferenceEquals(gpu.ProcessMaterial, process)
                && !subEmitter.Emitting && subEmitter.Amount == 11
                && !cpu.Emitting && cpu.Amount == 29 && cpu.Preprocess == 7.25 && cpu.Lifetime == 3.5
                && cpu.Gravity == new Vector2(0, 55) && cpu.InitialVelocityMin == 13 && cpu.InitialVelocityMax == 17,
                "Warming hidden stopped emitters must not restart, preprocess, resize or redirect either live source or its live sub-emitter.");
            Require(line.Width == 7 && line.Points.SequenceEqual(new[] { new Vector2(-9, 2), new Vector2(17, 2), new Vector2(17, 21) })
                && multimesh.InstanceCount == 3 && multimesh.GetInstanceTransform2D(2).Origin == new Vector2(100, 0)
                && group.GetChildCount() == 1 && group.FitMargin == 3 && group.ClearMargin == 5,
                "Representative native line, mesh and group extraction must not rewrite source geometry or children.");
            Require(sourceActivity == 0 && live.IsInsideTree() && live.GetParent() == this && live.Transform == liveTransform
                && !live.Visible && live.ProcessMode == ProcessModeEnum.Disabled && live.GetChildCount() == sources.Length + 1,
                "Native extraction must leave the live source tree and its lifecycle untouched.");
            RequireRenderWarmupHostRestored(viewport, baseline);
        }
        finally { live.Free(); }
    }

    private sealed record RenderWarmupRectState(ColorRect Node, Material Material, Vector2 Position, Vector2 Size);

    private static Node2D RenderWarmupCases(int firstCase, int count, out List<RenderWarmupRectState> originals)
    {
        var root = new Node2D { Visible = false, Position = new Vector2(913, -47), ProcessMode = ProcessModeEnum.Disabled };
        originals = new List<RenderWarmupRectState>();
        for (var index = 0; index < count; index++)
        {
            var rectangle = new ColorRect
            {
                Name = "ShaderCase" + index, Material = RenderWarmupMaterial(firstCase + index), Visible = false,
                Position = new Vector2(index * 17, index * 9), Size = new Vector2(17, 23),
                Modulate = new Color(0.3f, 0.4f, 0.5f, 0.25f),
            };
            root.AddChild(rectangle);
            originals.Add(new RenderWarmupRectState(rectangle, rectangle.Material, rectangle.Position, rectangle.Size));
        }
        return root;
    }

    private static void RequireRenderWarmupCasesUnchanged(Node2D root, List<RenderWarmupRectState> originals, Node parent)
    {
        Require(root.GetParent() == parent && root.GetChildCount() == originals.Count && !root.Visible
            && root.Position == new Vector2(913, -47) && root.ProcessMode == ProcessModeEnum.Disabled,
            "Warmup must not attach, detach, reveal or process the original source root.");
        foreach (var state in originals)
            Require(state.Node.GetParent() == root && ReferenceEquals(state.Node.Material, state.Material)
                && state.Node.Position == state.Position && state.Node.Size == state.Size && !state.Node.Visible
                && !state.Node.UseParentMaterial && state.Node.Modulate == new Color(0.3f, 0.4f, 0.5f, 0.25f),
                "Batching/cancellation/disposal must leave every original shader case unchanged.");
    }

    private static IEnumerable<Node> RenderWarmupDescendants(Node root)
    {
        foreach (var child in root.GetChildren(true))
        {
            yield return child;
            foreach (var descendant in RenderWarmupDescendants(child)) yield return descendant;
        }
    }

    private static bool RenderWarmupCase(CanvasItem node, out int fixtureCase)
    {
        fixtureCase = -1;
        if (node.Material is not ShaderMaterial material) return false;
        using var value = material.GetShaderParameter("fixture_case");
        if (value.VariantType != Variant.Type.Int) return false;
        fixtureCase = value.AsInt32();
        return true;
    }

    private async Task CheckRenderWarmupBatches(SubViewport viewport)
    {
        const int firstCase = 201;
        const int count = 13;
        var source = RenderWarmupCases(firstCase, count, out var originals);
        var baseline = RenderWarmupHostChildren(viewport);
        var frameCases = new List<(int Case, Rect2 Rect)>();
        var seenCases = new HashSet<int>();
        var pixelCases = new HashSet<int>();
        var cloneIds = new HashSet<ulong>();
        var observedPeak = 0;
        void BeforeDraw()
        {
            frameCases.Clear();
            foreach (var item in RenderWarmupDescendants(viewport).OfType<ColorRect>())
                if (RenderWarmupCase(item, out var fixtureCase) && fixtureCase >= firstCase && fixtureCase < firstCase + count)
                {
                    frameCases.Add((fixtureCase, item.GetGlobalRect()));
                    seenCases.Add(fixtureCase);
                    cloneIds.Add(item.GetInstanceId());
                }
            observedPeak = Math.Max(observedPeak, frameCases.Count);
        }
        void AfterDraw()
        {
            if (frameCases.Count == 0) return;
            using var image = viewport.GetTexture().GetImage();
            if (image == null || image.IsEmpty()) return;
            foreach (var item in frameCases)
                if (RenderWarmupPixelCount(image, item.Rect) >= 16) pixelCases.Add(item.Case);
        }
        RenderingServer.FramePreDraw += BeforeDraw;
        RenderingServer.FramePostDraw += AfterDraw;
        try
        {
            using (var warmup = new RenderReplicaWarmup(viewport, mask: false))
            {
                await warmup.WarmAsync(source);
                Require(warmup.Drawn == count && warmup.Failed == 0 && warmup.Skipped == 0
                    && warmup.PeakBatch > 0 && warmup.PeakBatch <= 6 && warmup.PostDraws >= 3,
                    "Thirteen distinct native shader cases must submit in batches no larger than six, with post-draw waits.");
                Require(observedPeak > 0 && observedPeak <= 6 && seenCases.Count == count && pixelCases.Count == count,
                    "Every distinct shader case must enter a bounded native batch and produce real viewport pixels while that batch is alive.");
                Require(cloneIds.Count >= count && cloneIds.All(id => !GodotObject.IsInstanceIdValid(id)),
                    "Submitted replicas must be freed after each batch, not retained as live draw nodes.");
                RequireRenderWarmupCasesUnchanged(source, originals, null);
            }
        }
        finally
        {
            RenderingServer.FramePreDraw -= BeforeDraw;
            RenderingServer.FramePostDraw -= AfterDraw;
            source.Free();
        }
        await RenderWarmupFrame();
        RequireRenderWarmupHostRestored(viewport, baseline);
    }

    private async Task CheckRenderWarmupCancellation(SubViewport viewport)
    {
        var source = RenderWarmupCases(301, 13, out var originals);
        AddChild(source);
        var baseline = RenderWarmupHostChildren(viewport);
        try
        {
            using (var inactive = new RenderReplicaWarmup(viewport, isActive: () => false))
            {
                await inactive.WarmAsync(source);
                Require(inactive.Drawn == 0 && inactive.PeakBatch == 0 && inactive.PostDraws == 0,
                    "An inactive session must not attach or submit any replicas.");
            }
            await RenderWarmupFrame();
            RequireRenderWarmupHostRestored(viewport, baseline);
            RequireRenderWarmupCasesUnchanged(source, originals, this);

            var active = true;
            var submittedFrames = 0;
            var preparedFrame = false;
            void BeforeCancellationDraw() { preparedFrame = true; }
            void CancelAfterDraw()
            {
                if (!preparedFrame) return;
                submittedFrames++;
                active = false;
            }
            RenderingServer.FramePreDraw += BeforeCancellationDraw;
            RenderingServer.FramePostDraw += CancelAfterDraw;
            try
            {
                using var cancelled = new RenderReplicaWarmup(viewport, isActive: () => active);
                await cancelled.WarmAsync(source);
                Require(submittedFrames > 0 && cancelled.PostDraws > 0 && cancelled.Drawn <= 6
                    && cancelled.PeakBatch > 0 && cancelled.PeakBatch <= 6 && cancelled.Failed == 0,
                    "Turning isActive off after a draw must stop before later batches without treating cancellation as extraction failure.");
                RequireRenderWarmupCasesUnchanged(source, originals, this);
            }
            finally
            {
                RenderingServer.FramePreDraw -= BeforeCancellationDraw;
                RenderingServer.FramePostDraw -= CancelAfterDraw;
            }
            await RenderWarmupFrame();
            RequireRenderWarmupHostRestored(viewport, baseline);

            var disposed = new RenderReplicaWarmup(viewport);
            var running = disposed.WarmAsync(source);
            var overlays = viewport.GetChildren(true).Where(node => !baseline.Contains(node.GetInstanceId())).ToArray();
            Require(!running.IsCompleted && overlays.Length > 0,
                "The disposal fixture must interrupt an actual pending native batch with an attached overlay.");
            disposed.Dispose();
            disposed.Dispose();
            await running;
            await RenderWarmupFrame();
            Require(overlays.All(node => !GodotObject.IsInstanceValid(node)),
                "Disposing a pending warmup must destroy its native overlay and support repeated disposal.");
            RequireRenderWarmupHostRestored(viewport, baseline);
            RequireRenderWarmupCasesUnchanged(source, originals, this);
        }
        finally { source.Free(); }
    }

    private static Sprite2D RenderWarmupPinSource()
    {
        var material = RenderWarmupMaterial(501);
        var texture = RenderWarmupWhiteTexture();
        return new Sprite2D { Material = material, Texture = texture, Visible = false };
    }

    private async Task CheckRenderWarmupPins(SubViewport viewport)
    {
        var source = RenderWarmupPinSource();
        var baseline = RenderWarmupHostChildren(viewport);
        var pinsBefore = RenderReplicaWarmup.PinnedResourceCount;
        var cloneIds = new HashSet<ulong>();
        var resourceIds = new HashSet<ulong>();
        void BeforeDraw()
        {
            foreach (var item in RenderWarmupDescendants(viewport).OfType<Sprite2D>())
                if (RenderWarmupCase(item, out var fixtureCase) && fixtureCase == 501)
                {
                    cloneIds.Add(item.GetInstanceId());
                    var material = (ShaderMaterial)item.Material;
                    resourceIds.Add(material.GetInstanceId());
                    resourceIds.Add(material.Shader.GetInstanceId());
                    resourceIds.Add(item.Texture.GetInstanceId());
                }
        }
        RenderingServer.FramePreDraw += BeforeDraw;
        var pinsAfter = 0;
        try
        {
            using (var warmup = new RenderReplicaWarmup(viewport, mask: false))
            {
                await warmup.WarmAsync(source);
                pinsAfter = RenderReplicaWarmup.PinnedResourceCount;
                Require(warmup.Drawn == 1 && warmup.Failed == 0 && resourceIds.Count >= 3
                    && pinsAfter >= pinsBefore + resourceIds.Count,
                    "A submitted sprite must pin its actual replica material, shader and texture resources.");
                Require(cloneIds.Count > 0 && cloneIds.All(id => !GodotObject.IsInstanceIdValid(id)),
                    "Resource pinning must not keep the native sprite clone alive.");
                Require(!source.Visible && !source.IsInsideTree() && source.GetParent() == null,
                    "Pinning must not attach or reveal the original sprite.");
            }
        }
        finally
        {
            RenderingServer.FramePreDraw -= BeforeDraw;
            source.Free();
        }
        await RenderWarmupFrame();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(RenderReplicaWarmup.PinnedResourceCount >= pinsAfter && resourceIds.All(GodotObject.IsInstanceIdValid),
            "Pinned render resources must remain natively alive after clones, overlay, source owner and temporary managed wrappers are gone.");
        RequireRenderWarmupHostRestored(viewport, baseline);
    }

    private static ulong[] RenderWarmupHostChildren(Node host) => host.GetChildren(true)
        .Select(node => node.GetInstanceId()).OrderBy(id => id).ToArray();

    private static void RequireRenderWarmupHostRestored(Node host, ulong[] baseline) =>
        Require(RenderWarmupHostChildren(host).SequenceEqual(baseline),
            "Warmup disposal must restore the host's original children without deleting or retaining unrelated nodes.");
}
