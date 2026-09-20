using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
namespace STS2Mobile;

public static class HarmonyMethodReferenceImporterShim
{
    private const string HarmonyId = "com.sts2mobile.monomod.importer";
    private const int RepairLogLimit = 32;
    private static bool _initialized;
    private static bool _importerInstalled;
    private static bool _emitterInstalled;
    private static int _repairLogCount;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            var diagnostic = DiagnoseDamageResultSetterImport();
            PatchHelper.Log($"[HarmonyImporterShim] DamageResult.set_UnblockedDamage import diagnostic: {diagnostic}");

            if (!diagnostic.NeedsShim)
            {
                PatchHelper.Log("[HarmonyImporterShim] method modifier repair not required.");
                return;
            }

            Install();
            var after = DiagnoseDamageResultSetterImport();
            PatchHelper.Log($"[HarmonyImporterShim] DamageResult.set_UnblockedDamage direct import after repair: {after}; importer={_importerInstalled}; emitter={_emitterInstalled}");
            if (after.NeedsShim && !_emitterInstalled)
                PatchHelper.Log("[HarmonyImporterShim] ERROR custom modifier repair is not active on either import path; Harmony wrappers may fail on Android/Mono.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyImporterShim] initialization failed: {exception}");
        }
    }

    private static void Install()
    {
        var harmony = new Harmony(HarmonyId);
        var genericProviderType = ResolveType("Mono.Cecil", "Mono.Cecil.IGenericParameterProvider");
        var importerType = ResolveType("MonoMod.Utils", "MonoMod.Utils.MMReflectionImporter");
        var importerTarget = genericProviderType == null || importerType == null
            ? null
            : importerType.GetMethod(
                "ImportReference",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(MethodBase), genericProviderType },
                null);

        if (importerTarget == null)
        {
            PatchHelper.Log($"[HarmonyImporterShim] SKIPPED MMReflectionImporter method import: importerType={importerType != null} genericProviderType={genericProviderType != null} target={importerTarget != null}");
        }
        else
        {
            try
            {
                var postfix = typeof(HarmonyMethodReferenceImporterShim).GetMethod(nameof(ImportReferencePostfix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(importerTarget, postfix: new HarmonyMethod(postfix));
                _importerInstalled = true;
                PatchHelper.Log("[HarmonyImporterShim] Patched MMReflectionImporter.ImportReference(MethodBase, IGenericParameterProvider).");
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"[HarmonyImporterShim] Failed to patch MMReflectionImporter method import: {exception.GetBaseException()}");
            }
        }

        var emitterTarget = ResolveCecilMethodImporter();
        if (emitterTarget == null)
        {
            PatchHelper.Log("[HarmonyImporterShim] SKIPPED CecilILGenerator method import helper: compatible method not found.");
            return;
        }

        try
        {
            var postfix = typeof(HarmonyMethodReferenceImporterShim).GetMethod(nameof(CecilMethodImportPostfix), BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(emitterTarget, postfix: new HarmonyMethod(postfix));
            _emitterInstalled = true;
            PatchHelper.Log("[HarmonyImporterShim] Patched CecilILGenerator MethodBase import result.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyImporterShim] Failed to patch CecilILGenerator method import helper: {exception.GetBaseException()}");
        }
    }

    private static void ImportReferencePostfix(object __instance, MethodBase __0, object __1, object __result)
    {
        TryRepairMethodReference(
            __0,
            __result,
            type => CallImporter(
                __instance,
                "ImportReference",
                new[] { typeof(Type), ResolveType("Mono.Cecil", "Mono.Cecil.IGenericParameterProvider") },
                type,
                __result));
    }

    private static void CecilMethodImportPostfix(MethodBase __0, object __result)
    {
        TryRepairMethodReference(__0, __result, type => ImportTypeFromMethodModule(__result, type));
    }

    private static void TryRepairMethodReference(MethodBase method, object methodReference, Func<Type, object> importType)
    {
        try
        {
            if (methodReference == null || !ShouldImportWithModifiers(method))
                return;

            if (!ApplyMethodReferenceModifiers(methodReference, method, importType))
                return;

            var repairNumber = Interlocked.Increment(ref _repairLogCount);
            if (repairNumber <= RepairLogLimit)
            {
                PatchHelper.Log($"[HarmonyImporterShim] Repaired STS2 method reference custom modifiers: {DescribeMethod(method)} -> {GetProperty(methodReference, "FullName")}");
            }
            else if (repairNumber == RepairLogLimit + 1)
            {
                PatchHelper.Log($"[HarmonyImporterShim] Further repaired method logs suppressed after {RepairLogLimit} entries.");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyImporterShim] Failed to repair imported STS2 method reference for {DescribeMethod(method)}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool ShouldImportWithModifiers(MethodBase method)
    {
        if (method == null || !IsSts2Method(method))
            return false;

        if (method is MethodInfo methodInfo && HasCustomModifiers(methodInfo.ReturnParameter))
            return true;

        return method.GetParameters().Any(HasCustomModifiers);
    }

    private static bool ApplyMethodReferenceModifiers(object methodReference, MethodBase method, Func<Type, object> importType)
    {
        var changed = false;
        var methodInfo = method as MethodInfo;
        var returnParameter = methodInfo?.ReturnParameter;
        if (returnParameter != null)
        {
            var currentReturnType = GetProperty(methodReference, "ReturnType");
            var repairedReturnType = RepairTypeReference(
                currentReturnType,
                returnParameter.GetRequiredCustomModifiers(),
                returnParameter.GetOptionalCustomModifiers(),
                importType);
            if (!ReferenceEquals(currentReturnType, repairedReturnType))
            {
                SetProperty(methodReference, "ReturnType", repairedReturnType);
                changed = true;
            }
        }

        if (GetProperty(methodReference, "Parameters") is not System.Collections.IEnumerable importedParameters)
            return changed;

        var originalParameters = method.GetParameters();
        var index = 0;
        foreach (var importedParameter in importedParameters)
        {
            if (index >= originalParameters.Length)
                break;

            var originalParameter = originalParameters[index++];
            var currentParameterType = GetProperty(importedParameter, "ParameterType");
            var repairedParameterType = RepairTypeReference(
                currentParameterType,
                originalParameter.GetRequiredCustomModifiers(),
                originalParameter.GetOptionalCustomModifiers(),
                importType);
            if (ReferenceEquals(currentParameterType, repairedParameterType))
                continue;

            SetProperty(importedParameter, "ParameterType", repairedParameterType);
            changed = true;
        }

        return changed;
    }

    private static object RepairTypeReference(
        object currentTypeReference,
        Type[] requiredModifiers,
        Type[] optionalModifiers,
        Func<Type, object> importType)
    {
        requiredModifiers ??= Type.EmptyTypes;
        optionalModifiers ??= Type.EmptyTypes;
        if (currentTypeReference == null || requiredModifiers.Length + optionalModifiers.Length == 0)
            return currentTypeReference;

        if (HasExpectedModifiers(currentTypeReference, requiredModifiers, optionalModifiers))
            return currentTypeReference;

        var requiredModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.RequiredModifierType")
            ?? throw new InvalidOperationException("Mono.Cecil.RequiredModifierType type missing");
        var optionalModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.OptionalModifierType")
            ?? throw new InvalidOperationException("Mono.Cecil.OptionalModifierType type missing");

        var repaired = StripModifierTypes(currentTypeReference, requiredModifierType, optionalModifierType);
        foreach (var modifierType in requiredModifiers)
            repaired = Activator.CreateInstance(requiredModifierType, importType(modifierType), repaired);
        foreach (var modifierType in optionalModifiers)
            repaired = Activator.CreateInstance(optionalModifierType, importType(modifierType), repaired);
        return repaired;
    }

    private static bool HasExpectedModifiers(object typeReference, Type[] requiredModifiers, Type[] optionalModifiers)
    {
        var requiredModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.RequiredModifierType");
        var optionalModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.OptionalModifierType");
        if (requiredModifierType == null || optionalModifierType == null)
            return false;

        var existingRequired = new List<string>();
        var existingOptional = new List<string>();
        var current = typeReference;
        while (current != null)
        {
            if (requiredModifierType.IsInstanceOfType(current))
                existingRequired.Add(DescribeTypeReference(GetProperty(current, "ModifierType")));
            else if (optionalModifierType.IsInstanceOfType(current))
                existingOptional.Add(DescribeTypeReference(GetProperty(current, "ModifierType")));
            else
                break;
            current = GetProperty(current, "ElementType");
        }

        return ModifierNamesMatch(existingRequired, requiredModifiers)
            && ModifierNamesMatch(existingOptional, optionalModifiers);
    }

    private static bool ModifierNamesMatch(IReadOnlyCollection<string> existing, IReadOnlyCollection<Type> expected)
    {
        if (existing.Count != expected.Count)
            return false;

        var remaining = new List<string>(existing);
        foreach (var modifier in expected)
        {
            var index = remaining.FindIndex(name => string.Equals(name, modifier.FullName, StringComparison.Ordinal));
            if (index < 0)
                return false;
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }

    private static object StripModifierTypes(object typeReference, Type requiredModifierType, Type optionalModifierType)
    {
        var current = typeReference;
        while (current != null
               && (requiredModifierType.IsInstanceOfType(current) || optionalModifierType.IsInstanceOfType(current)))
        {
            current = GetProperty(current, "ElementType");
        }
        return current;
    }

    private static string DescribeTypeReference(object typeReference)
    {
        return GetProperty(typeReference, "FullName") as string
            ?? GetProperty(typeReference, "Name") as string
            ?? typeReference?.ToString()
            ?? string.Empty;
    }

    private static object ImportTypeFromMethodModule(object methodReference, Type type)
    {
        var module = GetProperty(methodReference, "Module")
            ?? GetProperty(GetProperty(methodReference, "DeclaringType"), "Module")
            ?? throw new InvalidOperationException("Imported MethodReference has no ModuleDefinition");
        var genericProviderType = ResolveType("Mono.Cecil", "Mono.Cecil.IGenericParameterProvider")
            ?? throw new InvalidOperationException("Mono.Cecil.IGenericParameterProvider type missing");
        var method = module.GetType().GetMethod(
            "ImportReference",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(Type), genericProviderType },
            null) ?? throw new MissingMethodException(module.GetType().FullName, "ImportReference(Type, IGenericParameterProvider)");
        return method.Invoke(module, new[] { (object)type, methodReference });
    }

    private static ImportDiagnostic DiagnoseDamageResultSetterImport()
    {
        var importerType = ResolveType("MonoMod.Utils", "MonoMod.Utils.MMReflectionImporter");
        var providerType = ResolveType("Mono.Cecil", "Mono.Cecil.IReflectionImporterProvider");
        var moduleDefinitionType = ResolveType("Mono.Cecil", "Mono.Cecil.ModuleDefinition");
        var moduleParametersType = ResolveType("Mono.Cecil", "Mono.Cecil.ModuleParameters");
        var moduleKindType = ResolveType("Mono.Cecil", "Mono.Cecil.ModuleKind");
        if (importerType == null || providerType == null || moduleDefinitionType == null || moduleParametersType == null || moduleKindType == null)
            return ImportDiagnostic.Skipped($"required types missing importer={importerType != null} provider={providerType != null} module={moduleDefinitionType != null}");

        var damageResult = ResolveSts2Type("MegaCrit.Sts2.Core.Entities.Creatures.DamageResult");
        if (damageResult == null)
            return ImportDiagnostic.Skipped("DamageResult type not loaded");

        var setter = damageResult.GetProperty("UnblockedDamage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            ?.GetSetMethod(true);
        if (setter == null)
            return ImportDiagnostic.Skipped("UnblockedDamage setter not found");

        var parameters = Activator.CreateInstance(moduleParametersType);
        SetProperty(parameters, "Kind", Enum.Parse(moduleKindType, "Dll"));
        SetProperty(parameters, "ReflectionImporterProvider", GetStaticField(importerType, "ProviderNoDefault"));

        var module = moduleDefinitionType.GetMethod("CreateModule", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), moduleParametersType }, null)
            ?.Invoke(null, new[] { "sts2mobile_importer_probe", parameters });
        if (module == null)
            return ImportDiagnostic.Skipped("failed to create probe module");

        try
        {
            var imported = moduleDefinitionType.GetMethod("ImportReference", new[] { typeof(MethodBase) })?.Invoke(module, new object[] { setter });
            if (imported == null)
                return ImportDiagnostic.Skipped("probe import returned null");

            var importedReturnMods = CountModifiers(GetProperty(imported, "ReturnType"));
            var reflectionReturnMods = CountModifiers(setter.ReturnParameter);
            var importedParameters = GetProperty(imported, "Parameters");
            var importedParamMods = SumParameterModifierCounts(importedParameters);
            var reflectionParamMods = setter.GetParameters().Sum(CountModifiers);
            var needsShim = reflectionReturnMods + reflectionParamMods > importedReturnMods + importedParamMods;

            return new ImportDiagnostic(
                false,
                needsShim,
                $"setter={DescribeMethod(setter)} reflection_mods=return:{reflectionReturnMods},params:{reflectionParamMods} imported_mods=return:{importedReturnMods},params:{importedParamMods} imported={GetProperty(imported, "FullName")}");
        }
        finally
        {
            (module as IDisposable)?.Dispose();
        }
    }

    private static int SumParameterModifierCounts(object parameters)
    {
        if (parameters is not System.Collections.IEnumerable enumerable)
            return 0;

        var count = 0;
        foreach (var parameter in enumerable)
            count += CountModifiers(GetProperty(parameter, "ParameterType"));
        return count;
    }

    private static int CountModifiers(ParameterInfo parameter)
    {
        return parameter.GetRequiredCustomModifiers().Length + parameter.GetOptionalCustomModifiers().Length;
    }

    private static int CountModifiers(object typeReference)
    {
        var requiredModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.RequiredModifierType");
        var optionalModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.OptionalModifierType");
        var typeSpecificationType = ResolveType("Mono.Cecil", "Mono.Cecil.TypeSpecification");
        if (typeReference == null || requiredModifierType == null || optionalModifierType == null || typeSpecificationType == null)
            return 0;

        var count = 0;
        var current = typeReference;
        while (current != null && typeSpecificationType.IsInstanceOfType(current))
        {
            if (requiredModifierType.IsInstanceOfType(current) || optionalModifierType.IsInstanceOfType(current))
                count++;
            current = GetProperty(current, "ElementType");
        }
        return count;
    }

    private static bool HasCustomModifiers(ParameterInfo parameter)
    {
        return parameter != null
            && (parameter.GetRequiredCustomModifiers().Length != 0
                || parameter.GetOptionalCustomModifiers().Length != 0);
    }

    private static object CallImporter(object importer, string methodName, Type[] parameterTypes, params object[] args)
    {
        var method = importer.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, parameterTypes, null)
            ?? throw new MissingMethodException(importer.GetType().FullName, methodName);
        return method.Invoke(importer, args);
    }

    private static object GetStaticField(Type type, string name)
    {
        return type?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static object GetProperty(object instance, string name)
    {
        return instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
    }

    private static void SetProperty(object instance, string name, object value)
    {
        instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(instance, value);
    }


    private static MethodInfo ResolveCecilMethodImporter()
    {
        var generatorType = ResolveType("MonoMod.Utils", "MonoMod.Utils.Cil.CecilILGenerator");
        if (generatorType == null)
            return null;

        return generatorType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "_"
                    && string.Equals(method.ReturnType.FullName, "Mono.Cecil.MethodReference", StringComparison.Ordinal)
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(MethodBase);
            });
    }

    private static Type ResolveType(string assemblyName, string fullName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        var type = loaded?.GetType(fullName, throwOnError: false);
        if (type != null)
            return type;

        type = Type.GetType(fullName + ", " + assemblyName, throwOnError: false);
        if (type != null)
            return type;

        try
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            return assembly.GetType(fullName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSts2Method(MethodBase method)
    {
        try
        {
            return string.Equals(method.Module.Assembly.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Type ResolveSts2Type(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase))
                continue;
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type != null)
                return type;
        }
        return Type.GetType(fullName + ", sts2", throwOnError: false);
    }

    private static string DescribeMethod(MethodBase method)
    {
        if (method == null)
            return "<null>";
        return $"{method.DeclaringType?.FullName ?? "<module>"}.{method.Name}";
    }

    private readonly struct ImportDiagnostic
    {
        public readonly bool WasSkipped;
        public readonly bool NeedsShim;
        private readonly string _message;

        public ImportDiagnostic(bool wasSkipped, bool needsShim, string message)
        {
            WasSkipped = wasSkipped;
            NeedsShim = needsShim;
            _message = message;
        }

        public static ImportDiagnostic Skipped(string reason) => new ImportDiagnostic(true, false, reason);

        public override string ToString()
        {
            return WasSkipped
                ? $"skipped reason={_message}"
                : $"needs_shim={NeedsShim} {_message}";
        }
    }
}
