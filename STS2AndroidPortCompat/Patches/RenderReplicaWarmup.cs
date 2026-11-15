using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Patches;

// Only native rendering nodes enter the tree. Never Duplicate() a scripted node,
// change a live emitter, or keep a temporary particle buffer alive as a cache.
internal sealed class RenderReplicaWarmup : IDisposable
{
    private const int BatchSize = 6;
    private static readonly Dictionary<ulong, Resource> PinnedResources = new();
    private static readonly HashSet<DrawKey> Submitted = new();
    private static Texture2D _white;
    private readonly Node _host;
    private readonly SceneTree _tree;
    private readonly CanvasLayer _layer;
    private readonly Node2D _surface;
    private readonly Func<bool> _isActive;
    private readonly List<(CanvasItem Node, DrawKey Key)> _batch = new(BatchSize);
    private readonly HashSet<DrawKey> _pending = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private bool _disposed;

    internal int Drawn { get; private set; }
    internal int Skipped { get; private set; }
    internal int Failed { get; private set; }
    internal int PeakBatch { get; private set; }
    internal int PostDraws { get; private set; }
    internal int Reused { get; private set; }
    internal int Scripted { get; private set; }
    internal static int PinnedResourceCount => PinnedResources.Count;

    internal RenderReplicaWarmup(Node host, bool mask = true, Func<bool> isActive = null)
    {
        _host = host;
        _tree = host.GetTree();
        _isActive = isActive;
        _layer = new CanvasLayer { Name = "AndroidRenderReplicaWarmup", Layer = 100 };
        _surface = new Node2D();
        _layer.AddChild(_surface);
        if (mask)
        {
            // Draw replicas first, then cover them. Hidden, transparent or
            // offscreen replicas would bypass the renderer we need to exercise.
            var cover = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Stop };
            cover.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(cover);
            var label = new Label
            {
                Text = "Preparing rendering resources…",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(label);
        }
        host.AddChild(_layer);
    }

    private bool Active => !_disposed && Valid(_host) && _host.IsInsideTree()
        && !_host.IsQueuedForDeletion() && Valid(_layer) && _layer.IsInsideTree()
        && (_isActive?.Invoke() ?? true);

    internal async Task WarmAsync(Node source)
    {
        if (!Active || !Valid(source))
            return;
        var nodes = new Stack<Node>();
        nodes.Push(source);
        int visited = 0;
        while (nodes.Count != 0 && Active)
        {
            Node node = nodes.Pop();
            if (!Valid(node) || node.IsQueuedForDeletion() || node == _layer)
                continue;
            for (int i = node.GetChildCount() - 1; i >= 0; i--)
                nodes.Push(node.GetChild(i));
            using (var script = node.GetScript())
                if (script.VariantType != Variant.Type.Nil)
                    Scripted++;
            if (node is CanvasItem item)
            {
                CanvasItem replica = null;
                try
                {
                    if (item is GpuParticles2D emitter && !emitter.SubEmitter.IsEmpty)
                        Report("subemitter_chain_not_replayed", item);
                    replica = CreateReplica(item, out string reason);
                    if (replica == null)
                    {
                        if (reason != null)
                        {
                            Skipped++;
                            Report(reason, item);
                        }
                    }
                    else
                    {
                        var key = KeyFor(replica);
                        if (Submitted.Contains(key) || !_pending.Add(key))
                        {
                            Reused++;
                            replica.Free();
                        }
                        else
                        {
                            _batch.Add((replica, key));
                            replica = null;
                            if (_batch.Count == BatchSize)
                                await FlushAsync();
                        }
                    }
                }
                catch (Exception exception)
                {
                    Failed++;
                    if (Valid(replica))
                        replica.Free();
                    Report("extraction_failed:" + exception.GetType().Name, item);
                }
            }
            // Bound even scans consisting entirely of containers/cache hits.
            if (++visited % 32 == 0 && Active)
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        }
        await FlushAsync();
    }

    private async Task FlushAsync()
    {
        if (_batch.Count == 0)
            return;
        try
        {
            if (!Active)
                return;
            Vector2 size = _host.GetViewport().GetVisibleRect().Size;
            for (int i = 0; i < _batch.Count; i++)
            {
                var node = _batch[i].Node;
                _surface.AddChild(node);
                Vector2 position = size * 0.5f + new Vector2((i % 3 - 1) * 80, (i / 3) * 80 - 40);
                if (node is Node2D node2D)
                    node2D.Position += position;
                else if (node is Control control)
                    control.Position += position - control.Size * 0.5f;
                if (node is GpuParticles2D gpu)
                {
                    gpu.Restart();
                    // Force a simulation step even on a fast/high-refresh frame.
                    gpu.RequestParticlesProcess(1.0f / 30.0f);
                }
                else if (node is CpuParticles2D cpu)
                    cpu.Restart();
            }
            PeakBatch = Math.Max(PeakBatch, _batch.Count);
            // A complete process iteration before each post-draw avoids accepting
            // the tail of the frame that was already in progress when we attached.
            // This is render submission evidence, NOT a GPU fence/cache flush.
            for (int frame = 0; frame < 2 && Active; frame++)
            {
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
                if (!Active)
                    return;
                await _tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                PostDraws++;
            }
            if (!Active)
                return;
            foreach (var entry in _batch)
            {
                PinVisual(entry.Node);
                Submitted.Add(entry.Key);
                Drawn++;
            }
        }
        finally
        {
            foreach (var entry in _batch)
                if (Valid(entry.Node))
                    entry.Node.Free();
            _batch.Clear();
            _pending.Clear();
        }
    }

    internal static CanvasItem CreateReplica(CanvasItem source, out string reason)
    {
        reason = null;
        CanvasItem replica;
        switch (source)
        {
            case GpuParticles2D gpu:
                if (gpu.ProcessMaterial == null)
                {
                    reason = "gpu_process_material_missing";
                    return null;
                }
                Material processMaterial = gpu.ProcessMaterial;
                ParticleProcessMaterial isolatedProcess = null;
                if (processMaterial is ParticleProcessMaterial originalProcess && !originalProcess.ParticleFlagDisableZ
                    && originalProcess.Gravity == new Vector3(0, -9.8f, 0))
                {
                    // GPUParticles2D.set_process_material auto-normalizes this
                    // exact shape in-place. Isolate only that case, then restore
                    // its actual shader shape after assigning the copy.
                    isolatedProcess = (ParticleProcessMaterial)originalProcess.Duplicate(false);
                    processMaterial = isolatedProcess;
                }
                replica = new GpuParticles2D
                {
                    Amount = 2, AmountRatio = 1, OneShot = false, Emitting = true,
                    Lifetime = 0.1, Explosiveness = 1, Preprocess = 0, SpeedScale = 1,
                    FixedFps = 0, LocalCoords = true,
                    VisibilityRect = new Rect2(-1024, -1024, 2048, 2048),
                    Texture = gpu.Texture ?? WhiteTexture(), ProcessMaterial = processMaterial,
                    DrawOrder = gpu.DrawOrder, TrailEnabled = gpu.TrailEnabled,
                    TrailLifetime = Math.Min(gpu.TrailLifetime, 0.1),
                    TrailSections = gpu.TrailSections, TrailSectionSubdivisions = gpu.TrailSectionSubdivisions,
                    // Never copy a NodePath that could target a live sub-emitter.
                };
                if (isolatedProcess != null)
                {
                    isolatedProcess.ParticleFlagDisableZ = false;
                    isolatedProcess.Gravity = new Vector3(0, -9.8f, 0);
                }
                break;
            case CpuParticles2D cpu:
                replica = new CpuParticles2D
                {
                    Amount = 2, OneShot = false, Emitting = true, Lifetime = 0.1,
                    Explosiveness = 1, Preprocess = 0, SpeedScale = 1, FixedFps = 0,
                    LocalCoords = true, Gravity = Vector2.Zero,
                    Texture = cpu.Texture ?? WhiteTexture(), DrawOrder = cpu.DrawOrder,
                };
                break;
            case Line2D line:
                var copy = new Line2D
                {
                    Texture = line.Texture, TextureMode = line.TextureMode, Width = 10,
                    JointMode = line.JointMode, BeginCapMode = line.BeginCapMode,
                    EndCapMode = line.EndCapMode, Antialiased = line.Antialiased,
                };
                copy.AddPoint(new Vector2(-24, -16));
                copy.AddPoint(new Vector2(8, -16));
                copy.AddPoint(new Vector2(24, 16));
                replica = copy;
                break;
            case Sprite2D sprite:
                var spriteCopy = new Sprite2D
                {
                    Texture = sprite.Texture ?? WhiteTexture(), Centered = true,
                    Hframes = sprite.Hframes, Vframes = sprite.Vframes, Frame = sprite.Frame,
                    RegionEnabled = sprite.RegionEnabled, RegionRect = sprite.RegionRect,
                    RegionFilterClipEnabled = sprite.RegionFilterClipEnabled,
                    FlipH = sprite.FlipH, FlipV = sprite.FlipV,
                };
                Fit(spriteCopy, spriteCopy.GetRect());
                replica = spriteCopy;
                break;
            case MeshInstance2D mesh:
                if (mesh.Mesh == null)
                {
                    reason = "mesh_generated_at_runtime";
                    return null;
                }
                var meshCopy = new MeshInstance2D { Mesh = mesh.Mesh, Texture = mesh.Texture };
                var aabb = mesh.Mesh.GetAabb();
                Fit(meshCopy, new Rect2(aabb.Position.X, aabb.Position.Y, aabb.Size.X, aabb.Size.Y));
                replica = meshCopy;
                break;
            case MultiMeshInstance2D multi:
                if (multi.Multimesh?.Mesh == null || multi.Multimesh.TransformFormat != MultiMesh.TransformFormatEnum.Transform2D)
                {
                    reason = "multimesh_missing_or_non2d";
                    return null;
                }
                var buffer = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                    UseColors = multi.Multimesh.UseColors, UseCustomData = multi.Multimesh.UseCustomData,
                    Mesh = multi.Multimesh.Mesh, InstanceCount = 1,
                };
                buffer.SetInstanceTransform2D(0, Transform2D.Identity);
                if (buffer.UseColors) buffer.SetInstanceColor(0, Colors.White);
                if (buffer.UseCustomData) buffer.SetInstanceCustomData(0, Colors.White);
                replica = new MultiMeshInstance2D { Multimesh = buffer, Texture = multi.Texture };
                break;
            case Polygon2D polygon:
                if (!polygon.Skeleton.IsEmpty)
                {
                    reason = "skinned_polygon_requires_skeleton";
                    return null;
                }
                Vector2[] points = polygon.Polygon;
                if (points.Length < 3)
                {
                    reason = "polygon_generated_at_runtime";
                    return null;
                }
                var polygonCopy = new Polygon2D { Polygon = points, UV = polygon.UV, Texture = polygon.Texture };
                var bounds = new Rect2(points[0], Vector2.Zero);
                foreach (Vector2 point in points)
                    bounds = bounds.Expand(point);
                Fit(polygonCopy, bounds);
                replica = polygonCopy;
                break;
            case CanvasGroup group:
                var groupCopy = new CanvasGroup { FitMargin = group.FitMargin, ClearMargin = group.ClearMargin, UseMipmaps = group.UseMipmaps };
                groupCopy.AddChild(new ColorRect { Position = new Vector2(-32, -32), Size = new Vector2(64, 64), Color = Colors.White });
                replica = groupCopy;
                break;
            case ColorRect:
                replica = new ColorRect { Size = new Vector2(64, 64), Color = Colors.White };
                break;
            case NinePatchRect patch:
                replica = new NinePatchRect
                {
                    Texture = patch.Texture ?? WhiteTexture(), Size = new Vector2(64, 64),
                    RegionRect = patch.RegionRect, DrawCenter = patch.DrawCenter,
                    PatchMarginLeft = patch.PatchMarginLeft, PatchMarginTop = patch.PatchMarginTop,
                    PatchMarginRight = patch.PatchMarginRight, PatchMarginBottom = patch.PatchMarginBottom,
                    AxisStretchHorizontal = patch.AxisStretchHorizontal, AxisStretchVertical = patch.AxisStretchVertical,
                };
                break;
            case TextureRect texture:
                replica = new TextureRect
                {
                    Texture = texture.Texture ?? WhiteTexture(), Size = new Vector2(64, 64),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                };
                break;
            case Label label:
                var labelCopy = new Label { Text = "Ag0123456789", Size = new Vector2(128, 64), LabelSettings = label.LabelSettings };
                labelCopy.AddThemeFontOverride("font", label.GetThemeFont("font"));
                labelCopy.AddThemeFontSizeOverride("font_size", label.GetThemeFontSize("font_size"));
                replica = labelCopy;
                break;
            default:
                // Plain containers do not draw. Native extensions/custom _Draw
                // cannot be safely impersonated by a rectangle or a particle.
                string nativeClass = source.GetClass();
                if (nativeClass != "Node2D" && nativeClass != "Control")
                    reason = "unsupported_draw_type:" + nativeClass;
                return null;
        }
        replica.Material = ShaderCompatibilityPatches.ResolveWarmupMaterial(EffectiveMaterial(source));
        replica.UseParentMaterial = false;
        replica.Modulate = Colors.White;
        replica.SelfModulate = Colors.White;
        replica.Visible = true;
        replica.LightMask = source.LightMask;
        replica.TextureFilter = source.TextureFilter;
        replica.TextureRepeat = source.TextureRepeat;
        for (CanvasItem parent = ParentItem(source); parent != null; parent = ParentItem(parent))
        {
            if (replica.TextureFilter == CanvasItem.TextureFilterEnum.ParentNode)
                replica.TextureFilter = parent.TextureFilter;
            if (replica.TextureRepeat == CanvasItem.TextureRepeatEnum.ParentNode)
                replica.TextureRepeat = parent.TextureRepeat;
            if (replica.TextureFilter != CanvasItem.TextureFilterEnum.ParentNode
                && replica.TextureRepeat != CanvasItem.TextureRepeatEnum.ParentNode)
                break;
        }
        if (replica is Control control)
            control.MouseFilter = Control.MouseFilterEnum.Ignore;
        return replica;
    }

    private static Material EffectiveMaterial(CanvasItem source)
    {
        CanvasItem current = source;
        // Godot stops inheritance at top-level items, CanvasLayer and ordinary
        // non-CanvasItem parents. With no parent item, the local material wins.
        while (current.UseParentMaterial && ParentItem(current) is CanvasItem parent)
            current = parent;
        return current.Material;
    }

    private static CanvasItem ParentItem(CanvasItem item) => item.TopLevel ? null : item.GetParent() as CanvasItem;

    private static void Fit(Node2D node, Rect2 bounds)
    {
        float side = Math.Max(Math.Abs(bounds.Size.X), Math.Abs(bounds.Size.Y));
        float scale = side > 0 ? 64 / side : 1;
        node.Scale = new Vector2(scale, scale);
        node.Position = -(bounds.Position + bounds.Size * 0.5f) * scale;
    }

    private static Texture2D WhiteTexture()
    {
        if (_white == null)
        {
            using var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
            image.Fill(Colors.White);
            _white = ImageTexture.CreateFromImage(image);
        }
        return _white;
    }

    private static DrawKey KeyFor(CanvasItem item)
    {
        Material material = item.Material;
        ulong identity = material is ShaderMaterial shader ? Id(shader.Shader) : Id(material);
        int flags = ((int)item.TextureFilter << 16) | ((int)item.TextureRepeat << 20);
        Resource process = null, texture = null, geometry = null;
        switch (item)
        {
            case GpuParticles2D gpu:
                process = gpu.ProcessMaterial; texture = gpu.Texture;
                flags |= gpu.TrailEnabled ? 1 : 0;
                flags |= (int)gpu.DrawOrder << 4;
                break;
            case CpuParticles2D cpu: texture = cpu.Texture; flags |= (int)cpu.DrawOrder << 4; break;
            case Line2D line:
                texture = line.Texture;
                flags |= (int)line.TextureMode | ((int)line.JointMode << 2) | ((int)line.BeginCapMode << 4)
                    | ((int)line.EndCapMode << 6) | (line.Antialiased ? 256 : 0);
                break;
            case Sprite2D sprite: texture = sprite.Texture; break;
            case MeshInstance2D mesh: texture = mesh.Texture; geometry = mesh.Mesh; break;
            case MultiMeshInstance2D multi:
                texture = multi.Texture; geometry = multi.Multimesh.Mesh;
                flags |= (multi.Multimesh.UseColors ? 1 : 0) | (multi.Multimesh.UseCustomData ? 2 : 0);
                break;
            case Polygon2D polygon: texture = polygon.Texture; break;
            case NinePatchRect patch: texture = patch.Texture; break;
            case TextureRect rect: texture = rect.Texture; break;
            case Label label: texture = label.GetThemeFont("font"); break;
            case CanvasGroup group: flags |= group.UseMipmaps ? 1 : 0; break;
        }
        // Shader identity (not merely source text) is intentional: a newly
        // created shader has a different native program/cache lifetime.
        return new DrawKey(item.GetType(), identity, Id(process), Id(texture), flags, Id(geometry), item.LightMask);
    }

    private static void PinVisual(CanvasItem item)
    {
        PinMaterial(item.Material);
        switch (item)
        {
            case GpuParticles2D gpu: PinMaterial(gpu.ProcessMaterial); Pin(gpu.Texture); break;
            case CpuParticles2D cpu: Pin(cpu.Texture); break;
            case Sprite2D sprite: Pin(sprite.Texture); break;
            case Line2D line: Pin(line.Texture); break;
            case MeshInstance2D mesh: Pin(mesh.Mesh); Pin(mesh.Texture); break;
            // Keep the mesh, not the throwaway MultiMesh instance buffer.
            case MultiMeshInstance2D multi: Pin(multi.Multimesh.Mesh); Pin(multi.Texture); break;
            case Polygon2D polygon: Pin(polygon.Texture); break;
            case TextureRect rect: Pin(rect.Texture); break;
            case NinePatchRect patch: Pin(patch.Texture); break;
            case Label label: Pin(label.GetThemeFont("font")); Pin(label.LabelSettings); break;
        }
    }

    private static void PinMaterial(Material material)
    {
        Pin(material);
        if (material is ShaderMaterial shader)
            Pin(shader.Shader);
    }

    private static void Pin(Resource resource)
    {
        if (Valid(resource))
            PinnedResources.TryAdd(resource.GetInstanceId(), resource);
    }

    private void Report(string reason, CanvasItem source)
    {
        if (_reported.Add(reason))
            PatchHelper.Log($"Render replica coverage gap: reason={reason} node={source.Name}; static extraction only.");
    }

    internal void LogSummary(string scope)
    {
        PatchHelper.Log($"Render replica warmup: scope={scope} submitted={Drawn} reused={Reused} skipped={Skipped} failed={Failed} scripted_static_only={Scripted} peak_batch={PeakBatch} post_draws={PostDraws} pinned_resources={PinnedResourceCount}; submission is not GPU completion or disk-cache proof.");
    }

    private static bool Valid(GodotObject value) => value != null && GodotObject.IsInstanceValid(value);
    private static ulong Id(GodotObject value) => Valid(value) ? value.GetInstanceId() : 0;
    private readonly record struct DrawKey(Type Kind, ulong Material, ulong Process, ulong Texture, int Flags, ulong Geometry, int LightMask);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var entry in _batch)
            if (Valid(entry.Node))
                entry.Node.Free();
        _batch.Clear();
        _pending.Clear();
        if (Valid(_layer))
            _layer.QueueFree();
    }
}
