using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace STS2Mobile.Patches;

/// <summary>
/// Defers user-MOD Harmony patches for STS2 types whose static constructors
/// can read UI/localization or ModelDb before essential initialization.
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

    private static MethodInfo _patchClassProcessPatchJobMethod;
    private static FieldInfo _patchClassHarmonyField;
    private static FieldInfo _patchClassContainerTypeField;
    private static FieldInfo _patchClassAuxiliaryMethodsField;

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

        var directProcessorInstalled = false;
        var patchClassProcessorInstalled = false;
        var targetFactoryInstalled = false;

        try
        {
            CachePatchProcessorFields();
            var target = AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.Patch), Type.EmptyTypes);
            var prefix = AccessTools.Method(typeof(DeferredModPatchQueue), nameof(PatchProcessorPatchPrefix));
            if (target == null || prefix == null)
                throw new MissingMethodException("PatchProcessor.Patch deferral hook not found.");

            harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            directProcessorInstalled = true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed to install direct PatchProcessor.Patch guard: {exception}");
        }

        try
        {
            CachePatchClassProcessorMembers();
            var prefix = AccessTools.Method(typeof(DeferredModPatchQueue), nameof(PatchClassProcessorProcessPatchJobPrefix));
            if (_patchClassProcessPatchJobMethod == null || prefix == null)
                throw new MissingMethodException("PatchClassProcessor.ProcessPatchJob deferral hook not found.");

            harmony.Patch(
                _patchClassProcessPatchJobMethod,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            patchClassProcessorInstalled = true;
            harmony.Patch(
                AccessTools.Method(typeof(PatchClassProcessor), nameof(PatchClassProcessor.Patch), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(DeferredModPatchQueue), nameof(PatchClassProcessorPatchPrefix)) { priority = Priority.First });
            targetFactoryInstalled = true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed to install PatchAll/PatchClassProcessor job guard: {exception}");
        }

        _applied = directProcessorInstalled || patchClassProcessorInstalled;
        if (_applied)
        {
            PatchHelper.Log(
                $"{PatchLabel}: installed user-MOD Harmony patch deferral guards for early Android/Mono cctor safety "
                + $"(direct_processor={directProcessorInstalled}, patch_all_jobs={patchClassProcessorInstalled}, target_factories={targetFactoryInstalled}).");
        }
        else
        {
            PatchHelper.Log($"{PatchLabel}: no Harmony patch entrypoint guard could be installed; deferred user-MOD patch queue disabled.");
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

    // TargetMethods runs before ProcessPatchJob. Defer the complete processor
    // before Prepare/target discovery if its factory reads uninitialized models.
    public static bool PatchClassProcessorPatchPrefix(PatchClassProcessor __instance, ref List<MethodInfo> __result)
    {
        if (!ShouldInspectPatch())
            return true;
        try
        {
            var auxiliary = _patchClassAuxiliaryMethodsField.GetValue(__instance) as Dictionary<Type, MethodInfo>;
            if (auxiliary == null)
                return true;
            auxiliary.TryGetValue(typeof(HarmonyTargetMethods), out var factory);
            if (factory == null)
                auxiliary.TryGetValue(typeof(HarmonyTargetMethod), out factory);
            if (factory == null || !ReadsModelContent(factory, factory.Module.Assembly, new HashSet<MethodBase>()))
                return true;
            var harmony = _patchClassHarmonyField.GetValue(__instance) as Harmony;
            if (harmony == null)
                return true;
            var patch = new DeferredPatch
            {
                Kind = DeferredPatchKind.PatchClassProcessor,
                Harmony = harmony,
                HarmonyId = harmony.Id,
                Original = factory,
                PatchClassProcessor = __instance,
                PatchClassName = factory.DeclaringType?.FullName,
                ModId = CurrentModId(),
                Reason = "target factory reads ModelDb content before essential initialization"
            };
            Enqueue(ref patch);
            __result = new List<MethodInfo>();
            PatchHelper.Log($"{PatchLabel}: deferred target factory #{patch.Order} {DescribeMethod(factory)} from {patch.HarmonyId} during {patch.ModId}.");
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed to inspect target factory; retaining immediate Harmony behavior: {exception}");
            return true;
        }
    }

    private static bool ReadsModelContent(MethodBase method, Assembly sourceAssembly, HashSet<MethodBase> visited)
    {
        if (method == null || !visited.Add(method) || method.GetMethodBody() == null)
            return false;
        // Iterator factories return before their body runs; inspect MoveNext too.
        var iterator = method.GetCustomAttribute<IteratorStateMachineAttribute>();
        var moveNext = iterator?.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (ReadsModelContent(moveNext, sourceAssembly, visited))
            return true;
        foreach (var instruction in PatchProcessor.ReadMethodBody(method))
        {
            if (instruction.Value is FieldInfo field
                && (instruction.Key == OpCodes.Ldsfld || instruction.Key == OpCodes.Ldsflda)
                && CanInspectDependency(field.Module.Assembly, sourceAssembly)
                && ReadsModelContent(field.DeclaringType?.TypeInitializer, sourceAssembly, visited))
                return true;
            if (instruction.Value is not MethodBase called
                || (instruction.Key != OpCodes.Call && instruction.Key != OpCodes.Callvirt
                    && instruction.Key != OpCodes.Ldftn && instruction.Key != OpCodes.Ldvirtftn
                    && instruction.Key != OpCodes.Newobj))
                continue;
            if (called.DeclaringType == typeof(ModelDb))
            {
                // Id computation/type discovery and Contains are registration-safe;
                // model and collection reads require canonical construction.
                if (called.Name.StartsWith("get_", StringComparison.Ordinal)
                    && called.Name != "get_AllAbstractModelSubtypes")
                    return true;
                if (called.Name == "Get" || called.Name == "GetById" || called.Name == "GetByIdOrNull"
                    || (called is MethodInfo lookup && lookup.IsGenericMethod
                        && typeof(AbstractModel).IsAssignableFrom(lookup.ReturnType)))
                    return true;
                continue;
            }
            if (called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Modding.ModHelper"
                && called.Name == "ConcatModelsFromMods")
                return true;
            if (CanInspectDependency(called.Module.Assembly, sourceAssembly)
                && (ReadsModelContent(called.DeclaringType?.TypeInitializer, sourceAssembly, visited)
                    || ReadsModelContent(called, sourceAssembly, visited)))
                return true;
        }
        return false;
    }

    private static bool CanInspectDependency(Assembly assembly, Assembly sourceAssembly)
    {
        return assembly == sourceAssembly || IsSts2Assembly(assembly);
    }

    public static bool PatchProcessorPatchPrefix(PatchProcessor __instance, ref MethodInfo __result)
    {
        if (!ShouldInspectPatch())
            return true;

        try
        {
            if (!TryCreateDeferredPatch(__instance, out var deferredPatch))
                return true;

            Enqueue(ref deferredPatch);
            __result = null;
            PatchHelper.Log(
                $"{PatchLabel}: deferred direct user-MOD patch #{deferredPatch.Order} for {DescribeMethod(deferredPatch.Original)} from {deferredPatch.HarmonyId} during {deferredPatch.ModId}; reason={deferredPatch.Reason}");
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed while inspecting PatchProcessor.Patch; falling back to immediate patch: {exception}");
            return true;
        }
    }

    /// <summary>
    /// PatchAll does not call PatchProcessor.Patch. Ekyso Harmony builds one
    /// private PatchClassProcessor job per original and sends it directly to
    /// PatchFunctions.UpdateWrapper. Intercept the job before its per-target
    /// prepare/apply/cleanup sequence and retain the exact processor + job so
    /// Harmony can execute that sequence unchanged when the queue is flushed.
    /// </summary>
    public static bool PatchClassProcessorProcessPatchJobPrefix(PatchClassProcessor __instance, object __0)
    {
        if (!ShouldInspectPatch())
            return true;

        try
        {
            if (!TryCreateDeferredPatchClassJob(__instance, __0, out var deferredPatch))
                return true;

            Enqueue(ref deferredPatch);
            PatchHelper.Log(
                $"{PatchLabel}: deferred PatchAll job #{deferredPatch.Order} for {DescribeMethod(deferredPatch.Original)} from {deferredPatch.HarmonyId} during {deferredPatch.ModId}; patch_class={deferredPatch.PatchClassName}; reason={deferredPatch.Reason}");
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{PatchLabel}: failed while inspecting PatchClassProcessor job; falling back to immediate patch: {exception}");
            return true;
        }
    }

    private static void Enqueue(ref DeferredPatch deferredPatch)
    {
        lock (LockObject)
        {
            deferredPatch.Order = _nextOrder++;
            Queue.Add(deferredPatch);
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
            if (patch.Kind == DeferredPatchKind.PatchClassProcessor)
            {
                patch.PatchClassProcessor.Patch();
                return true;
            }
            if (patch.Kind == DeferredPatchKind.PatchClassProcessorJob)
            {
                // Invoke the original Harmony job object instead of reconstructing
                // it. This preserves all patch methods in a multi-method class,
                // owner/order metadata, inner patches, and the per-target
                // HarmonyPrepare/HarmonyCleanup flow. _flushing prevents our own
                // ProcessPatchJob prefix from queueing it a second time.
                _patchClassProcessPatchJobMethod.Invoke(patch.PatchClassProcessor, new[] { patch.PatchClassJob });
                return true;
            }

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
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            PatchHelper.Log($"{PatchLabel}: failed to replay deferred patch #{patch.Order} for {DescribeMethod(patch.Original)} from {patch.HarmonyId}: {exception.InnerException}");
            return false;
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

    private static bool ShouldInspectPatch()
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
            Kind = DeferredPatchKind.PatchProcessor,
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

    private static bool TryCreateDeferredPatchClassJob(
        PatchClassProcessor processor,
        object job,
        out DeferredPatch deferredPatch)
    {
        deferredPatch = default;
        if (processor == null || job == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var originalField = job.GetType().GetField("original", flags);
        var original = originalField?.GetValue(job) as MethodBase;
        if (original == null)
            return false;

        if (!ShouldDeferTarget(original.DeclaringType, out var reason))
            return false;

        var harmony = _patchClassHarmonyField.GetValue(processor) as Harmony;
        if (harmony == null)
            return false;

        var patchClass = _patchClassContainerTypeField?.GetValue(processor) as Type;
        deferredPatch = new DeferredPatch
        {
            Kind = DeferredPatchKind.PatchClassProcessorJob,
            Harmony = harmony,
            HarmonyId = harmony.Id ?? "<unknown>",
            Original = original,
            PatchClassProcessor = processor,
            PatchClassJob = job,
            PatchClassName = patchClass?.FullName ?? "<unknown>",
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

        if (typeof(AbstractModel).IsAssignableFrom(targetType))
        {
            reason = $"target {targetType.FullName} is a model with a static initializer before ModelDb.Init";
            return true;
        }

        if (IsGodotUiOrNodeType(targetType) || IsGodotUiOrNodeType(rootType))
        {
            reason = $"target {targetType.FullName} is an STS2 Godot/UI type with a static initializer";
            return true;
        }

        var visited = new HashSet<MethodBase>();
        for (var type = targetType; type != null; type = type.DeclaringType)
        {
            if (!ReadsModelContent(type.TypeInitializer, targetType.Assembly, visited))
                continue;
            reason = $"target {targetType.FullName} has static initialization that reads ModelDb content or consumes a model pool";
            return true;
        }

        reason = $"target {targetType.FullName} has no early-unsafe model/UI/Godot or model-content initialization";
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

    private static void CachePatchClassProcessorMembers()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _patchClassProcessPatchJobMethod = typeof(PatchClassProcessor).GetMethod("ProcessPatchJob", flags)
            ?? throw new MissingMethodException(typeof(PatchClassProcessor).FullName, "ProcessPatchJob");
        _patchClassHarmonyField = typeof(PatchClassProcessor).GetField("instance", flags)
            ?? throw new MissingFieldException(typeof(PatchClassProcessor).FullName, "instance");
        _patchClassContainerTypeField = typeof(PatchClassProcessor).GetField("containerType", flags);
        _patchClassAuxiliaryMethodsField = typeof(PatchClassProcessor).GetField("auxilaryMethods", flags)
            ?? throw new MissingFieldException(typeof(PatchClassProcessor).FullName, "auxilaryMethods");
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

    private enum DeferredPatchKind
    {
        PatchProcessor,
        PatchClassProcessorJob,
        PatchClassProcessor
    }

    private struct DeferredPatch
    {
        public DeferredPatchKind Kind;
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
        public PatchClassProcessor PatchClassProcessor;
        public object PatchClassJob;
        public string PatchClassName;
        public string ModId;
        public string Reason;
    }
}
