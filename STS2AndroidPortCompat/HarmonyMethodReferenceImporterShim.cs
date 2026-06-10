using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile;

public static class HarmonyMethodReferenceImporterShim
{
    private const string HarmonyId = "com.sts2mobile.monomod.importer";
    private static bool _initialized;
    private static bool _installed;

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
                PatchHelper.Log("[HarmonyImporterShim] MMReflectionImporter method modifier shim not required.");
                return;
            }

            Install();
            var after = DiagnoseDamageResultSetterImport();
            PatchHelper.Log($"[HarmonyImporterShim] DamageResult.set_UnblockedDamage import after shim: {after}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyImporterShim] initialization failed: {exception}");
        }
    }

    private static void Install()
    {
        if (_installed)
            return;

        var importerType = ResolveType("MonoMod.Utils", "MonoMod.Utils.MMReflectionImporter");
        var genericProviderType = ResolveType("Mono.Cecil", "Mono.Cecil.IGenericParameterProvider");
        if (importerType == null || genericProviderType == null)
        {
            PatchHelper.Log($"[HarmonyImporterShim] SKIPPED importer patch: importerType={importerType != null} genericProviderType={genericProviderType != null}");
            return;
        }

        var target = importerType.GetMethod(
            "ImportReference",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(MethodBase), genericProviderType },
            null);
        if (target == null)
        {
            PatchHelper.Log("[HarmonyImporterShim] SKIPPED MMReflectionImporter.ImportReference(MethodBase, IGenericParameterProvider): method not found");
            return;
        }

        var postfix = typeof(HarmonyMethodReferenceImporterShim).GetMethod(nameof(ImportReferencePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(postfix));
        _installed = true;
        PatchHelper.Log("[HarmonyImporterShim] Patched MonoMod.Utils.MMReflectionImporter.ImportReference(MethodBase, IGenericParameterProvider)");
    }

    private static void ImportReferencePostfix(object __instance, MethodBase method, object context, object __result)
    {
        try
        {
            if (__result == null || !ShouldImportWithModifiers(method))
                return;

            ApplyMethodReferenceModifiers(__instance, __result, method, context);
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

        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (HasCustomModifiers(parameters[i]))
                return true;
        }
        return false;
    }

    private static void ApplyMethodReferenceModifiers(object importer, object methodReference, MethodBase method, object context)
    {
        SetProperty(methodReference, "ReturnType", ImportReturnType(importer, method, methodReference));

        var importedParameters = GetProperty(methodReference, "Parameters") as System.Collections.IEnumerable;
        if (importedParameters == null)
            return;

        var originalParameters = method.GetParameters();
        var index = 0;
        foreach (var importedParameter in importedParameters)
        {
            if (index >= originalParameters.Length)
                break;

            var originalParameter = originalParameters[index++];
            SetProperty(
                importedParameter,
                "ParameterType",
                ImportTypeWithModifiers(
                    importer,
                    originalParameter.ParameterType,
                    originalParameter.GetRequiredCustomModifiers(),
                    originalParameter.GetOptionalCustomModifiers(),
                    methodReference));
        }

        PatchHelper.Log($"[HarmonyImporterShim] Repaired STS2 method reference custom modifiers: {DescribeMethod(method)} -> {GetProperty(methodReference, "FullName")}");
    }

    private static object ImportReturnType(object importer, MethodBase method, object context)
    {
        var methodInfo = method as MethodInfo;
        var returnType = methodInfo?.ReturnType ?? typeof(void);
        var returnParameter = methodInfo?.ReturnParameter;
        return ImportTypeWithModifiers(
            importer,
            returnType,
            returnParameter?.GetRequiredCustomModifiers(),
            returnParameter?.GetOptionalCustomModifiers(),
            context);
    }

    private static object ImportTypeWithModifiers(
        object importer,
        Type type,
        Type[] requiredModifiers,
        Type[] optionalModifiers,
        object context)
    {
        var genericProviderType = ResolveType("Mono.Cecil", "Mono.Cecil.IGenericParameterProvider");
        var typeReference = CallImporter(importer, "ImportReference", new[] { typeof(Type), genericProviderType }, type, context);

        if (requiredModifiers != null)
        {
            var requiredModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.RequiredModifierType") ?? throw new InvalidOperationException("Mono.Cecil.RequiredModifierType type missing");
            for (var i = 0; i < requiredModifiers.Length; i++)
            {
                var modifier = CallImporter(importer, "ImportReference", new[] { typeof(Type), genericProviderType }, requiredModifiers[i], context);
                typeReference = Activator.CreateInstance(requiredModifierType, modifier, typeReference);
            }
        }

        if (optionalModifiers != null)
        {
            var optionalModifierType = ResolveType("Mono.Cecil", "Mono.Cecil.OptionalModifierType") ?? throw new InvalidOperationException("Mono.Cecil.OptionalModifierType type missing");
            for (var i = 0; i < optionalModifiers.Length; i++)
            {
                var modifier = CallImporter(importer, "ImportReference", new[] { typeof(Type), genericProviderType }, optionalModifiers[i], context);
                typeReference = Activator.CreateInstance(optionalModifierType, modifier, typeReference);
            }
        }

        return typeReference;
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

    private static object Invoke(object instance, string methodName, params object[] args)
    {
        return instance?.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(instance, args);
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
