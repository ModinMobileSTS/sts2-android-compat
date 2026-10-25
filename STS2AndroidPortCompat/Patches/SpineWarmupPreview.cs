using System;
using Godot;

namespace STS2Mobile.Patches;

// A new native sprite owns its skeleton, state and generated mesh children. Only
// read-only resources/attachments are shared with the live creature; no scene
// children, scripts or game animation callbacks cross this boundary.
internal sealed class SpineWarmupPreview : IDisposable
{
    private const int ManualUpdateMode = 2;

    private readonly Node2D _node;
    private GodotObject _data;
    private GodotObject _skeleton;
    private GodotObject _state;
    private GodotObject _skin;
    private GodotObject _track;
    private StringName _setAnimation;
    private StringName _setMixDuration;
    private StringName _getAnimationEnd;
    private StringName _setTrackTime;
    private StringName _updateSkeleton;
    private bool _interrupted;
    private bool _disposed;

    private SpineWarmupPreview(Node2D node, GodotObject data)
    {
        _node = node;
        _data = data;
        _node.TreeExiting += OnTreeExiting;
    }

    internal Node2D Node => _node;

    internal static SpineWarmupPreview Create(Node2D source)
    {
        RequireMainThread();
        using var spriteClass = new StringName("SpineSprite");
        if (source == null || !GodotObject.IsInstanceValid(source)
            || source.IsQueuedForDeletion() || !source.IsClass(spriteClass))
            return null;

        // These objects can already be held by MegaSpine/cache consumers. Their
        // result Variants are ours, but their managed bindings are borrowed.
        using var dataValue = Call(source, "get_skeleton_data_res");
        GodotObject data = ObjectValue(dataValue);
        if (data == null)
            return null;
        RequireClass(data, "SpineSkeletonDataResource");
        using var loadedValue = Call(data, "is_skeleton_data_loaded");
        if (loadedValue.VariantType != Variant.Type.Bool)
            throw new InvalidOperationException("Spine skeleton data returned an unknown load status.");
        if (!loadedValue.AsBool())
            return null;
        using var sourceSkeletonValue = Call(source, "get_skeleton");
        GodotObject sourceSkeleton = ObjectValue(sourceSkeletonValue);
        if (sourceSkeleton == null)
            return null;
        RequireClass(sourceSkeleton, "SpineSkeleton");
        if (!ClassDB.CanInstantiate(spriteClass))
            throw new InvalidOperationException("Native SpineSprite construction is unavailable.");

        using var instanceValue = ClassDB.Instantiate(spriteClass);
        GodotObject instance = ObjectValue(instanceValue);
        SpineWarmupPreview preview = null;
        bool initialized = false;
        try
        {
            if (instance is not Node2D node || !node.IsClass(spriteClass))
                throw new InvalidOperationException("Native SpineSprite construction returned an unexpected object.");
            preview = new SpineWarmupPreview(node, data);
            preview.Initialize(source, sourceSkeleton);
            initialized = true;
            return preview;
        }
        finally
        {
            if (!initialized)
            {
                if (preview != null)
                    preview.Dispose();
                else if (instance != null && GodotObject.IsInstanceValid(instance))
                    instance.Free();
            }
            GC.KeepAlive(sourceSkeleton);
            GC.KeepAlive(data);
            GC.KeepAlive(source);
        }
    }

    private void Initialize(Node2D source, GodotObject sourceSkeleton)
    {
        _setAnimation = new StringName("set_animation");
        _setMixDuration = new StringName("set_mix_duration");
        _getAnimationEnd = new StringName("get_animation_end");
        _setTrackTime = new StringName("set_track_time");
        _updateSkeleton = new StringName("update_skeleton");
        RequireMethod(_node, _updateSkeleton);

        Call(_node, "set_update_mode", [ManualUpdateMode]).Dispose();
        using var dataArgument = Variant.From(_data);
        Call(_node, "set_skeleton_data_res", [dataArgument]).Dispose();
        using var skeletonValue = Call(_node, "get_skeleton");
        _skeleton = ObjectValue(skeletonValue);
        RequireClass(_skeleton, "SpineSkeleton");
        using var stateValue = Call(_node, "get_animation_state");
        _state = ObjectValue(stateValue);
        RequireClass(_state, "SpineAnimationState");
        RequireMethod(_state, _setAnimation);

        CopyValue(source, _node, "get_normal_material", "set_normal_material");
        CopyValue(source, _node, "get_additive_material", "set_additive_material");
        CopyValue(source, _node, "get_multiply_material", "set_multiply_material");
        CopyValue(source, _node, "get_screen_material", "set_screen_material");
        CopyValue(sourceSkeleton, _skeleton, "get_color", "set_color");
        CopyValue(sourceSkeleton, _skeleton, "get_x", "set_x");
        CopyValue(sourceSkeleton, _skeleton, "get_y", "set_y");
        CopyValue(sourceSkeleton, _skeleton, "get_scale_x", "set_scale_x");
        CopyValue(sourceSkeleton, _skeleton, "get_scale_y", "set_scale_y");

        // get_skin creates a transient wrapper even for a composed live skin.
        // Copy its map into our own skin: the live skin can then disappear while
        // immutable attachments remain referenced by the preview's native skin.
        using var skinValue = Call(sourceSkeleton, "get_skin");
        using GodotObject sourceSkin = ObjectValue(skinValue);
        if (sourceSkin != null)
        {
            RequireClass(sourceSkin, "SpineSkin");
            using var nameValue = Call(sourceSkin, "get_name");
            if (nameValue.VariantType != Variant.Type.String)
                throw new InvalidOperationException("Spine skin returned an unknown name type.");
            using var previewSkinValue = Call(_node, "new_skin", [nameValue]);
            _skin = ObjectValue(previewSkinValue);
            RequireClass(_skin, "SpineSkin");
            using var sourceSkinArgument = Variant.From(sourceSkin);
            Call(_skin, "add_skin", [sourceSkinArgument]).Dispose();
            using var previewSkinArgument = Variant.From(_skin);
            Call(_skeleton, "set_skin", [previewSkinArgument]).Dispose();
            Call(_skeleton, "set_slots_to_setup_pose").Dispose();
        }

        // This is a world-transform snapshot while unparented. The caller
        // restores it after attaching the preview to the room.
        _node.Transform = source.GlobalTransform;
        _node.Modulate = source.Modulate;
        _node.SelfModulate = source.SelfModulate;
        _node.Visible = source.Visible;
        _node.TextureFilter = source.TextureFilter;
        _node.TextureRepeat = source.TextureRepeat;
        _node.LightMask = source.LightMask;
        _node.VisibilityLayer = source.VisibilityLayer;
        _node.ShowBehindParent = source.ShowBehindParent;
        _node.ClipChildren = source.ClipChildren;
        Material material = source.Material;
        _node.Material = material;
        _node.UseParentMaterial = source.UseParentMaterial;
        _node.ZIndex = 0;
        _node.ZAsRelative = false;
        GC.KeepAlive(material);
        GC.KeepAlive(sourceSkin);
        GC.KeepAlive(sourceSkeleton);
        GC.KeepAlive(source);
    }

    internal string[] GetAnimationNames()
    {
        EnsureAvailable();
        using var animationsValue = Call(_data, "get_animations");
        if (animationsValue.VariantType != Variant.Type.Array)
            throw new InvalidOperationException("Spine animation enumeration returned an unexpected value.");
        using var animations = animationsValue.AsGodotArray();
        int next = 0;
        try
        {
            var names = new string[animations.Count];
            while (next < names.Length)
            {
                int index = next++;
                using var animationValue = animations[index];
                using GodotObject animation = ObjectValue(animationValue);
                RequireClass(animation, "SpineAnimation");
                using var nameValue = Call(animation, "get_name");
                if (nameValue.VariantType != Variant.Type.String)
                    throw new InvalidOperationException("Spine animation returned an unknown name type.");
                names[index] = nameValue.AsString();
            }
            return names;
        }
        finally
        {
            // Native enumeration creates every wrapper eagerly. Even an error
            // must release the unvisited tail here, never on the finalizer thread.
            while (next < animations.Count)
            {
                using var animationValue = animations[next++];
                using GodotObject animation = ObjectValue(animationValue);
            }
            GC.KeepAlive(animations);
            GC.KeepAlive(_data);
        }
    }

    internal float Play(string animationName, bool loop)
    {
        EnsureAvailable();
        GodotObject previousTrack = _track;
        _track = null;
        previousTrack?.Dispose();
        using var nameValue = Variant.From(animationName);
        using var trackValue = Invoke(_state, _setAnimation, [nameValue, loop, 0]);
        _track = ObjectValue(trackValue);
        RequireClass(_track, "SpineTrackEntry");
        RequireMethod(_track, _setMixDuration);
        RequireMethod(_track, _getAnimationEnd);
        RequireMethod(_track, _setTrackTime);
        Invoke(_track, _setMixDuration, [0f]).Dispose();
        using var durationValue = Invoke(_track, _getAnimationEnd);
        if (durationValue.VariantType != Variant.Type.Float)
            throw new InvalidOperationException("Spine animation returned an unknown duration type.");
        return durationValue.AsSingle();
    }

    internal void Sample(float trackTime)
    {
        EnsureAvailable();
        if (_track == null)
            throw new InvalidOperationException("A preview animation must be selected before sampling.");
        Invoke(_track, _setTrackTime, [trackTime]).Dispose();
        // Manual update applies only this sprite's state, then updates world
        // transforms and the meshes that Spine itself created and owns.
        Invoke(_node, _updateSkeleton, [0f]).Dispose();
        GC.KeepAlive(this);
    }

    public void Dispose()
    {
        RequireMainThread();
        if (_disposed)
            return;
        _disposed = true;
        _interrupted = true;
        try
        {
            if (GodotObject.IsInstanceValid(_node))
                _node.TreeExiting -= OnTreeExiting;
            ReleaseOwnedBindings();
        }
        finally
        {
            try
            {
                // No mesh child is ever removed/freed separately. In particular,
                // Spine keeps raw pointers to these otherwise ordinary children.
                if (GodotObject.IsInstanceValid(_node))
                    _node.Free();
            }
            finally
            {
                _setAnimation?.Dispose();
                _setMixDuration?.Dispose();
                _getAnimationEnd?.Dispose();
                _setTrackTime?.Dispose();
                _updateSkeleton?.Dispose();
                // Shared resource binding: release our managed reference only.
                _data = null;
            }
        }
    }

    private void OnTreeExiting()
    {
        // Parent destruction can happen during an awaited frame. Release the
        // preview-only bindings before native destruction and reject resumes.
        RequireMainThread();
        _interrupted = true;
        ReleaseOwnedBindings();
    }

    private void ReleaseOwnedBindings()
    {
        GodotObject track = _track;
        GodotObject state = _state;
        GodotObject skeleton = _skeleton;
        GodotObject skin = _skin;
        _track = null;
        _state = null;
        _skeleton = null;
        _skin = null;
        try
        {
            track?.Dispose();
        }
        finally
        {
            try
            {
                state?.Dispose();
            }
            finally
            {
                try
                {
                    skeleton?.Dispose();
                }
                finally
                {
                    skin?.Dispose();
                }
            }
        }
    }

    private void EnsureAvailable()
    {
        RequireMainThread();
        if (_disposed || _interrupted || !GodotObject.IsInstanceValid(_node) || _node.IsQueuedForDeletion())
            throw new ObjectDisposedException(nameof(SpineWarmupPreview));
    }

    private static void RequireMainThread()
    {
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            throw new InvalidOperationException("Spine warmup preview bindings must stay on the Godot main thread.");
    }

    private static GodotObject ObjectValue(Variant value)
    {
        if (value.VariantType == Variant.Type.Nil)
            return null;
        if (value.VariantType != Variant.Type.Object)
            throw new InvalidOperationException($"Expected a Spine object, got {value.VariantType}.");
        return value.AsGodotObject();
    }

    private static void RequireClass(GodotObject value, string className)
    {
        using var nativeClass = new StringName(className);
        bool matches = value != null && GodotObject.IsInstanceValid(value) && value.IsClass(nativeClass);
        GC.KeepAlive(value);
        if (!matches)
            throw new InvalidOperationException($"Expected a native {className} binding.");
    }

    private static void RequireMethod(GodotObject target, StringName method)
    {
        bool available = target.HasMethod(method);
        GC.KeepAlive(target);
        if (!available)
            throw new InvalidOperationException($"Native Spine API is missing {method}.");
    }

    private static void CopyValue(GodotObject source, GodotObject target, string getter, string setter)
    {
        using var value = Call(source, getter);
        Call(target, setter, [value]).Dispose();
        GC.KeepAlive(source);
    }

    private static Variant Call(GodotObject target, string methodName, ReadOnlySpan<Variant> arguments = default)
    {
        using var method = new StringName(methodName);
        RequireMethod(target, method);
        return Invoke(target, method, arguments);
    }

    private static Variant Invoke(GodotObject target, StringName method, ReadOnlySpan<Variant> arguments = default)
    {
        try
        {
            return target.Call(method, arguments);
        }
        finally
        {
            // Native-created RefCounted receivers can otherwise be finalized
            // even while their last managed use is still inside a native call.
            GC.KeepAlive(target);
            GC.KeepAlive(method);
        }
    }
}
