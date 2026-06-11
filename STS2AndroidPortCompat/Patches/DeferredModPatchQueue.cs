using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2Mobile.Patches;

/// <summary>
/// Defers user-MOD Harmony patches for STS2 Godot/UI classes whose static
/// constructors are unsafe during ModManager.Initialize on Android/Mono.
/// </summary>
public static class DeferredModPatchQueue
{
    private const string PatchLabel = "DeferredModPatchQueue";
    private static readonly object LockObject = new();
    private static readonly List<DeferredPatch> Queue = new();

    private static FieldInfo _processorHarmonyField;
    private static FieldInfo _processorOriginalField;
    private static FieldInfo _processorPrefixField;
    private static FieldInfo _processorPostfixField;
    private static FieldInfo _processorTranspilerField;
    private static FieldInfo _processorFinalizerField;
    private static FieldInfo _processorInnerPrefixField;
    private static FieldInfo _processorInnerPostfixField;

    private static bool _applied;
    private static int _modInitializationDepth;
    private static string _currentModId;
    private static bool _flushing;
    private static bool _flushCompleted;
    private static int _nextOrder;

    public static void Apply(Harmony harmony)
    {
        if (_applied)
            return;

        try
        {
            CachePatchProcessorFields();
            var target = AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.Patch), Type.EmptyTypes);
            var prefix = AccessTools.Method(typeof(DeferredModPatchQueue), nameof(PatchProcessorPatchPrefix));
            if (target == null || prefix == null)
            {
                PatchHelper.Log($"{PatchLabel}: PatchProcessor.Patch or prefix not found; deferred user-MOD patch queue disabled.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            _applied = true;
            PatchHelper.Log($"{PatchLabel}: installed user-MOD Harmony patch deferral guard for early Android/Mono cctor safety.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed to install guard: {exception}");
        }
    }

    public static IDisposable BeginModInitialization(string modId)
    {
        lock (LockObject)
        {
            _modInitializationDepth++;
            _currentModId = string.IsNullOrWhiteSpace(modId) ? "<unknown>" : modId;
        }
        return new ModInitializationScope();
    }

    public static bool PatchProcessorPatchPrefix(PatchProcessor __instance, ref MethodInfo __result)
    {
        if (!ShouldInspectPatchProcessor())
            return true;

        try
        {
            if (!TryCreateDeferredPatch(__instance, out var deferredPatch))
                return true;

            lock (LockObject)
            {
                deferredPatch.Order = _nextOrder++;
                Queue.Add(deferredPatch);
            }

            __result = null;
            PatchHelper.Log(
                $"{PatchLabel}: deferred user-MOD patch #{deferredPatch.Order} for {DescribeMethod(deferredPatch.Original)} from {deferredPatch.HarmonyId} during {deferredPatch.ModId}; reason={deferredPatch.Reason}");
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed while inspecting PatchProcessor.Patch; falling back to immediate patch: {exception}");
            return true;
        }
    }

    public static void FlushDeferredPatches(string phase)
    {
        List<DeferredPatch> patches;
        lock (LockObject)
        {
            if (_flushing)
                return;
            _flushing = true;
            patches = new List<DeferredPatch>(Queue);
            Queue.Clear();
        }

        try
        {
            if (patches.Count == 0)
            {
                PatchHelper.Log($"{PatchLabel}: no deferred user-MOD patches to replay at {phase}.");
                _flushCompleted = true;
                return;
            }

            PatchHelper.Log($"{PatchLabel}: replaying {patches.Count} deferred user-MOD patch(es) at {phase}.");
            patches.Sort((left, right) => left.Order.CompareTo(right.Order));
            var failedCount = 0;
            foreach (var patch in patches)
            {
                if (!ReplayPatch(patch))
                    failedCount++;
            }

            if (failedCount == 0)
                PatchHelper.Log($"{PatchLabel}: replayed {patches.Count} deferred user-MOD patch(es) at {phase}.");
            else
                PatchHelper.Log($"{PatchLabel}: replayed deferred user-MOD patches at {phase}; failed={failedCount}/{patches.Count}.");
            _flushCompleted = true;
        }
        finally
        {
            lock (LockObject)
            {
                _flushing = false;
            }
        }
    }

    private static bool ReplayPatch(DeferredPatch patch)
    {
        try
        {
            var processor = patch.Harmony.CreateProcessor(patch.Original);
            processor.AddPrefix(patch.Prefix);
            processor.AddPostfix(patch.Postfix);
            processor.AddTranspiler(patch.Transpiler);
            processor.AddFinalizer(patch.Finalizer);
            AddOptionalPatch(processor, "AddInnerPrefix", patch.InnerPrefix);
            AddOptionalPatch(processor, "AddInnerPostfix", patch.InnerPostfix);
            processor.Patch();
            return true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed to replay deferred patch #{patch.Order} for {DescribeMethod(patch.Original)} from {patch.HarmonyId}: {exception}");
            return false;
        }
    }

    private static void AddOptionalPatch(PatchProcessor processor, string methodName, HarmonyMethod patch)
    {
        if (patch == null)
            return;

        var method = typeof(PatchProcessor).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(HarmonyMethod) }, null);
        method?.Invoke(processor, new object[] { patch });
    }

    private static bool ShouldInspectPatchProcessor()
    {
        lock (LockObject)
        {
            return _applied
                && !_flushing
                && !_flushCompleted
                && _modInitializationDepth > 0;
        }
    }

    private static bool TryCreateDeferredPatch(PatchProcessor processor, out DeferredPatch deferredPatch)
    {
        deferredPatch = default;

        var original = _processorOriginalField.GetValue(processor) as MethodBase;
        if (original == null)
            return false;

        var targetType = original.DeclaringType;
        if (!ShouldDeferTarget(targetType, out var reason))
            return false;

        var harmony = _processorHarmonyField.GetValue(processor) as Harmony;
        if (harmony == null)
            return false;

        var prefix = _processorPrefixField.GetValue(processor) as HarmonyMethod;
        var postfix = _processorPostfixField.GetValue(processor) as HarmonyMethod;
        var transpiler = _processorTranspilerField.GetValue(processor) as HarmonyMethod;
        var finalizer = _processorFinalizerField.GetValue(processor) as HarmonyMethod;
        var innerPrefix = _processorInnerPrefixField?.GetValue(processor) as HarmonyMethod;
        var innerPostfix = _processorInnerPostfixField?.GetValue(processor) as HarmonyMethod;
        if (prefix == null && postfix == null && transpiler == null && finalizer == null && innerPrefix == null
            && innerPostfix == null)
        {
            return false;
        }

        deferredPatch = new DeferredPatch
        {
            Harmony = harmony,
            HarmonyId = harmony.Id ?? "<unknown>",
            Original = original,
            Prefix = prefix,
            Postfix = postfix,
            Transpiler = transpiler,
            Finalizer = finalizer,
            InnerPrefix = innerPrefix,
            InnerPostfix = innerPostfix,
            ModId = CurrentModId(),
            Reason = reason
        };
        return true;
    }

    private static bool ShouldDeferTarget(Type targetType, out string reason)
    {
        reason = null;
        if (targetType == null)
        {
            reason = "target type is null";
            return false;
        }

        if (!IsSts2Assembly(targetType.Assembly))
        {
            reason = "target is not in sts2 assembly";
            return false;
        }

        var rootType = GetRootDeclaringType(targetType);
        if (!HasAnyTypeInitializer(targetType))
        {
            reason = $"target {targetType.FullName} has no static initializer";
            return false;
        }

        if (IsGodotUiOrNodeType(targetType) || IsGodotUiOrNodeType(rootType))
        {
            reason = $"target {targetType.FullName} is an STS2 Godot/UI type with a static initializer";
            return true;
        }

        reason = $"target {targetType.FullName} is not an early-unsafe UI/Godot type";
        return false;
    }

    private static bool IsGodotUiOrNodeType(Type type)
    {
        if (type == null)
            return false;

        var fullName = type.FullName ?? string.Empty;
        if (fullName.StartsWith("MegaCrit.Sts2.Core.Nodes.", StringComparison.Ordinal)
            || fullName.StartsWith("MegaCrit.Sts2.addons.", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return typeof(Node).IsAssignableFrom(type) || typeof(Control).IsAssignableFrom(type);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAnyTypeInitializer(Type type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            try
            {
                if (current.TypeInitializer != null)
                    return true;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    private static Type GetRootDeclaringType(Type type)
    {
        var current = type;
        while (current?.DeclaringType != null)
            current = current.DeclaringType;
        return current ?? type;
    }

    private static bool IsSts2Assembly(Assembly assembly)
    {
        try
        {
            return string.Equals(assembly?.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string CurrentModId()
    {
        lock (LockObject)
        {
            return _currentModId ?? "<unknown>";
        }
    }

    private static void CachePatchProcessorFields()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _processorHarmonyField = RequiredField("instance");
        _processorOriginalField = RequiredField("original");
        _processorPrefixField = RequiredField("prefix");
        _processorPostfixField = RequiredField("postfix");
        _processorTranspilerField = RequiredField("transpiler");
        _processorFinalizerField = RequiredField("finalizer");
        _processorInnerPrefixField = typeof(PatchProcessor).GetField("innerprefix", flags);
        _processorInnerPostfixField = typeof(PatchProcessor).GetField("innerpostfix", flags);

        static FieldInfo RequiredField(string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            return typeof(PatchProcessor).GetField(name, flags)
                ?? throw new MissingFieldException(typeof(PatchProcessor).FullName, name);
        }
    }

    private static string DescribeMethod(MethodBase method)
    {
        if (method == null)
            return "<null>";
        return $"{method.DeclaringType?.FullName ?? "<module>"}.{method.Name}";
    }

    private sealed class ModInitializationScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (LockObject)
            {
                _modInitializationDepth = Math.Max(0, _modInitializationDepth - 1);
                if (_modInitializationDepth == 0)
                    _currentModId = null;
            }
        }
    }

    private struct DeferredPatch
    {
        public int Order;
        public Harmony Harmony;
        public string HarmonyId;
        public MethodBase Original;
        public HarmonyMethod Prefix;
        public HarmonyMethod Postfix;
        public HarmonyMethod Transpiler;
        public HarmonyMethod Finalizer;
        public HarmonyMethod InnerPrefix;
        public HarmonyMethod InnerPostfix;
        public string ModId;
        public string Reason;
    }
}
