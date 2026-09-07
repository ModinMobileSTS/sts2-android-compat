using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace STS2Mobile.Patches;

// Only naturally completed, unmodified stock effects are retained. Factories,
// Ready, animation timing and cancellation remain owned by the original game.
internal static class CombatVfxPoolPatches
{
    private static readonly ConditionalWeakTable<Node, Entry> Entries = new();
    private static readonly ConditionalWeakTable<NCombatRoom, RoomPool> Rooms = new();
    private static readonly Dictionary<Type, Contract> Contracts = new();
    private static readonly AsyncLocal<Playback> CurrentPlayback = new();
    private static StringName ColorParameter;
    private static Harmony _harmony;
    private static bool _installed;

    internal static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(CombatVfxPoolPatches), nameof(GameReadyPostfix)));
    }

    private static void GameReadyPostfix()
    {
        if (_installed || OS.GetName() != "Android") return;
        _installed = true;
        try
        {
            ColorParameter = "color";
            Install(typeof(NDamageNumVfx), "AnimVfx", 16);
            Install(typeof(NHitSparkVfx), "FlashAndFree", 8);
            Install(typeof(NShivThrowVfx), "PlaySequence", 8);
            _harmony.Patch(AccessTools.Method(typeof(GodotTreeExtensions), "QueueFreeSafely", new[] { typeof(Node) }),
                prefix: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(QueueFreePrefix)));
            _harmony.Patch(AccessTools.Method(typeof(NShivThrowVfx), "ApplyTint"),
                prefix: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(TintPrefix)));
            PatchHelper.Log("Combat VFX reuse enabled: per-room retained limits damage=16 hit=8 shiv=8; overflow uses original allocation.");
        }
        catch (Exception exception)
        {
            // Partially installed factory hooks must not rent without the release contract.
            Contracts.Clear();
            PatchHelper.Log($"Combat VFX reuse unavailable: {exception}");
        }
    }

    private static void Install(Type type, string playback, int capacity)
    {
        var contract = new Contract(type, playback, capacity);
        foreach (var method in contract.Factories)
            _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(FactoryTranspiler)));
        _harmony.Patch(contract.Play, prefix: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(PlaybackPrefix)),
            finalizer: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(PlaybackFinalizer)));
        Contracts.Add(type, contract);
    }

    private static bool IsInstantiation(CodeInstruction instruction, Type type) =>
        instruction.operand is MethodInfo method && method.DeclaringType == typeof(PackedScene)
        && method.Name == "Instantiate" && method.IsGenericMethod && method.GetGenericArguments()[0] == type;

    private static IEnumerable<CodeInstruction> FactoryTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        foreach (var instruction in instructions)
        {
            if (IsInstantiation(instruction, __originalMethod.DeclaringType))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(CombatVfxPoolPatches), nameof(Rent)).MakeGenericMethod(__originalMethod.DeclaringType);
            }
            yield return instruction;
        }
    }

    private static T Rent<T>(PackedScene scene, PackedScene.GenEditState editState) where T : Node
    {
        var room = NCombatRoom.Instance;
        if (editState != PackedScene.GenEditState.Disabled || room == null || !GodotObject.IsInstanceValid(room)
            || !room.IsInsideTree() || !Contracts.TryGetValue(typeof(T), out var contract))
            return scene.Instantiate<T>(editState);
        var pool = Rooms.GetValue(room, static owner => new RoomPool(owner));
        var bucket = pool.GetBucket(contract);
        if (pool.Closed || !bucket.Allowed) return scene.Instantiate<T>(editState);
        ulong sceneId = scene.GetInstanceId();
        while (bucket.Free.TryPop(out var entry))
        {
            entry.InPool = false;
            if (!GodotObject.IsInstanceValid(entry.Node)) continue;
            if (entry.SceneId != sceneId || entry.Node.IsQueuedForDeletion() || !entry.Restore())
            {
                entry.Node.QueueFree();
                continue;
            }
            entry.Active = true;
            entry.Generation++;
            return (T)(Node)entry.Node;
        }
        var node = scene.Instantiate<T>(editState);
        if (node.GetType() == typeof(T) && node is Node2D canvas && Capture(canvas, out var snapshots))
        {
            var entry = new Entry(canvas, bucket, sceneId, snapshots);
            Entries.Add(node, entry);
            node.TreeExiting += entry.Exiting;
        }
        return node;
    }

    private static bool Capture(Node2D root, out Snapshot[] snapshots)
    {
        var result = new List<Snapshot>();
        bool Visit(Node node)
        {
            if (node != root)
            {
                var type = node.GetType();
                if (type != typeof(Node2D) && type != typeof(GpuParticles2D)
                    && type != typeof(MegaCrit.Sts2.addons.mega_text.MegaLabel)) return false;
                using var script = node.GetScript();
                if (type != typeof(MegaCrit.Sts2.addons.mega_text.MegaLabel)
                    && script.VariantType != Variant.Type.Nil) return false;
            }
            result.Add(new Snapshot((CanvasItem)node));
            for (int i = 0; i < node.GetChildCount(); i++) if (!Visit(node.GetChild(i))) return false;
            return true;
        }
        bool supported = Visit(root);
        snapshots = supported ? result.ToArray() : null;
        return supported;
    }

    private static void PlaybackPrefix(Node __instance, out PlaybackScope __state)
    {
        __state = default;
        if (!Entries.TryGetValue(__instance, out var entry) || !entry.Active) return;
        __state = new PlaybackScope(CurrentPlayback.Value, true);
        // ExecutionContext carries this immutable lease through the original async
        // state machine. An old continuation can never release a later rental.
        CurrentPlayback.Value = new Playback(entry, entry.Generation);
    }

    private static void PlaybackFinalizer(PlaybackScope __state)
    {
        if (__state.Changed) CurrentPlayback.Value = __state.Previous;
    }

    private static bool QueueFreePrefix(Node __0)
    {
        if (__0 == null || !Entries.TryGetValue(__0, out var entry)) return true;
        var playback = CurrentPlayback.Value;
        if (playback != null && ReferenceEquals(playback.Entry, entry))
        {
            if (entry.InPool || entry.Returning) return false;
            if (playback.Generation != entry.Generation)
                return !entry.Active; // Preserve external-detach cleanup, but never destroy a later rental.
        }
        if (!entry.Active || playback == null || !ReferenceEquals(playback.Entry, entry)
            || entry.Bucket.Owner.Closed || !GodotObject.IsInstanceValid(entry.Node) || entry.Node.IsQueuedForDeletion())
        {
            entry.Active = false;
            entry.Generation++;
            return true; // External cancellation/removal is not a reusable completion.
        }
        entry.Active = false;
        entry.Returning = true;
        // Match QueueFree's end-of-frame lifetime. Do not detach in the middle of
        // particle notifications or expose the rental before its task has returned.
        Callable.From(entry.Return).CallDeferred();
        return false;
    }

    private static bool TintPrefix(NShivThrowVfx __instance, Color __0)
    {
        if (!Entries.TryGetValue(__instance, out var entry) || !entry.Active) return true;
        var particles = entry.Bucket.Contract.TintedParticles(__instance);
        if (entry.Materials == null || entry.Materials.Length != particles.Count)
            entry.Materials = new ParticleProcessMaterial[particles.Count];
        for (int i = 0; i < particles.Count; i++)
        {
            var particle = particles[i];
            var material = particle.ProcessMaterial;
            if (entry.Materials[i] == null || !GodotObject.IsInstanceValid(entry.Materials[i])
                || material != entry.Materials[i])
            {
                // Keep the original per-particle material isolation, but duplicate
                // only when the instance does not already own this material.
                entry.Materials[i] = (ParticleProcessMaterial)material.Duplicate();
                particle.ProcessMaterial = entry.Materials[i];
            }
            entry.Materials[i].Set(ColorParameter, __0);
        }
        return false;
    }

    private sealed class Contract
    {
        internal readonly Type Type;
        internal readonly int Capacity;
        internal readonly MethodInfo Play;
        internal readonly MethodInfo[] Factories;
        internal readonly MethodInfo[] GuardedMethods;
        internal readonly AccessTools.FieldRef<NShivThrowVfx, Godot.Collections.Array<GpuParticles2D>> TintedParticles;
        internal readonly FieldInfo TransientField;

        internal Contract(Type type, string playback, int capacity)
        {
            Type = type;
            Capacity = capacity;
            Play = AccessTools.DeclaredMethod(type, playback) ?? throw new MissingMethodException(type.FullName, playback);
            if (Play.ReturnType != typeof(System.Threading.Tasks.Task)) throw new InvalidOperationException($"Unknown VFX playback contract: {Play}");
            Factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Create" && PatchProcessor.GetOriginalInstructions(method).Any(instruction => IsInstantiation(instruction, type))).ToArray();
            if (Factories.Length == 0) throw new MissingMethodException(type.FullName, "Create/Instantiate");
            GuardedMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name is "Create" or "_Ready" or "_ExitTree" or "ApplyTint" || method == Play).ToArray();
            if (type == typeof(NShivThrowVfx))
                TintedParticles = AccessTools.FieldRefAccess<NShivThrowVfx, Godot.Collections.Array<GpuParticles2D>>("_modulateParticles");
            TransientField = AccessTools.Field(type, type == typeof(NDamageNumVfx) ? "_tween" : type == typeof(NHitSparkVfx) ? "_creatureNode" : "_cts");
        }

        internal bool AllowsReuse() => GuardedMethods.All(method => Harmony.GetPatchInfo(method)?.Owners.All(owner => owner == _harmony.Id) != false);
    }

    private sealed class RoomPool
    {
        private readonly Dictionary<Type, Bucket> _buckets = new();
        internal bool Closed;
        internal RoomPool(NCombatRoom room) { room.TreeExiting += Close; }
        internal Bucket GetBucket(Contract contract)
        {
            if (!_buckets.TryGetValue(contract.Type, out var bucket))
            {
                bucket = new Bucket(this, contract);
                _buckets.Add(contract.Type, bucket);
            }
            return bucket;
        }
        private void Close()
        {
            Closed = true;
            foreach (var bucket in _buckets.Values)
                while (bucket.Free.TryPop(out var entry))
                    if (GodotObject.IsInstanceValid(entry.Node)) entry.Node.QueueFree();
            _buckets.Clear();
        }
    }

    private sealed class Bucket
    {
        internal readonly RoomPool Owner;
        internal readonly Contract Contract;
        internal readonly bool Allowed;
        internal readonly Stack<Entry> Free;
        internal Bucket(RoomPool owner, Contract contract)
        {
            Owner = owner;
            Contract = contract;
            Allowed = contract.AllowsReuse();
            Free = new Stack<Entry>(contract.Capacity);
        }
    }

    private sealed class Entry
    {
        internal readonly Node2D Node;
        internal readonly Bucket Bucket;
        internal readonly ulong SceneId;
        private readonly Snapshot[] _snapshots;
        internal ParticleProcessMaterial[] Materials;
        internal bool Active = true;
        internal bool Returning;
        internal bool InPool;
        internal long Generation = 1;
        internal Entry(Node2D node, Bucket bucket, ulong sceneId, Snapshot[] snapshots)
        {
            Node = node; Bucket = bucket; SceneId = sceneId; _snapshots = snapshots;
        }
        internal bool Restore()
        {
            foreach (var snapshot in _snapshots)
                if (!GodotObject.IsInstanceValid(snapshot.Node) || snapshot.Node.GetChildCount() != snapshot.ChildCount) return false;
            foreach (var snapshot in _snapshots) snapshot.Restore();
            return true;
        }
        internal void Exiting()
        {
            if (!Returning) { Active = false; Generation++; }
        }
        internal void Return()
        {
            if (!GodotObject.IsInstanceValid(Node)) return;
            if (!Returning || Bucket.Owner.Closed || Node.IsQueuedForDeletion() || Bucket.Free.Count >= Bucket.Contract.Capacity)
            {
                Returning = false;
                Node.QueueFree();
                return;
            }
            Node.GetParent()?.RemoveChild(Node);
            foreach (var snapshot in _snapshots)
                if (snapshot.Node is GpuParticles2D particles) particles.Emitting = false;
            Returning = false;
            var transient = Bucket.Contract.TransientField;
            if (transient?.GetValue(Node) is CancellationTokenSource cancellation) cancellation.Dispose();
            transient?.SetValue(Node, null);
            InPool = true;
            Bucket.Free.Push(this);
        }
    }

    private readonly struct Snapshot
    {
        internal readonly CanvasItem Node;
        internal readonly int ChildCount;
        private readonly Transform2D _transform;
        private readonly Vector2 _controlScale;
        private readonly Color _modulate, _selfModulate;
        private readonly bool _visible, _emitting;
        internal Snapshot(CanvasItem node)
        {
            Node = node;
            ChildCount = node.GetChildCount();
            _transform = node is Node2D spatial ? spatial.Transform : Transform2D.Identity;
            _controlScale = node is Control control ? control.Scale : Vector2.One;
            _modulate = node.Modulate; _selfModulate = node.SelfModulate; _visible = node.Visible;
            _emitting = node is GpuParticles2D particles && particles.Emitting;
        }
        internal void Restore()
        {
            if (Node is Node2D spatial) spatial.Transform = _transform;
            else if (Node is Control control) control.Scale = _controlScale;
            Node.Modulate = _modulate; Node.SelfModulate = _selfModulate; Node.Visible = _visible;
            if (Node is GpuParticles2D particles) particles.Emitting = _emitting;
            Node.RequestReady();
        }
    }

    private sealed record Playback(Entry Entry, long Generation);
    private readonly record struct PlaybackScope(Playback Previous, bool Changed);
}
