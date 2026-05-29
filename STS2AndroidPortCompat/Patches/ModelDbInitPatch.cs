using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace STS2Mobile.Patches;

/// <summary>
/// Replaces the original one-pass ModelDb.Init with launcher-style two-phase
/// initialization. The imported PC assembly constructs some encounter models
/// before the monster models referenced by their static fields are registered
/// (for example BowlbugsNormal -> BowlbugEgg), so raw ModelDb.Get<T>() lookups
/// can fail during type initialization. Pre-registering placeholders lets those
/// cross-model references resolve, then constructors initialize the same objects
/// in place.
/// </summary>
public static class ModelDbInitPatch
{
    private static bool _suppressContains;
    private static bool _modelDbInitPatched;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var initTarget = typeof(ModelDb).GetMethod(nameof(ModelDb.Init), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var initPrefix = typeof(ModelDbInitPatch).GetMethod(nameof(InitPrefix), BindingFlags.Public | BindingFlags.Static);
            if (initTarget == null || initPrefix == null)
            {
                PatchHelper.Log("FAILED ModelDb.Init: method not found");
            }
            else
            {
                // Keep this prefix last so BaseLib/RitsuLib/user-mod prefixes on ModelDb.Init still run.
                // Returning false then skips only the vanilla one-pass constructor loop; Harmony postfixes
                // still run, preserving mod lifecycle hooks that are attached to ModelDb.Init.
                harmony.Patch(initTarget, prefix: new HarmonyMethod(initPrefix) { priority = Priority.Last });
                _modelDbInitPatched = true;
                PatchHelper.Log("Patched ModelDb.Init (two-phase replacement at Priority.Last; preserves mod init hooks).");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED ModelDb.Init: {exception}");
        }

        try
        {
            var target = typeof(OneTimeInitialization).GetMethod("ExecuteEssential", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var prefix = typeof(ModelDbInitPatch).GetMethod(nameof(ExecuteEssentialPrefix), BindingFlags.Public | BindingFlags.Static);
            if (target == null || prefix == null)
            {
                PatchHelper.Log("FAILED OneTimeInitialization.ExecuteEssential: method not found");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.Last });
            PatchHelper.Log("Patched OneTimeInitialization.ExecuteEssential (delegates through patched ModelDb.Init for mod hook compatibility).");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED OneTimeInitialization.ExecuteEssential: {exception}");
        }
    }

    public static bool ContainsPrefix(ref bool __result)
    {
        if (_suppressContains)
        {
            __result = false;
            return false;
        }
        return true;
    }

    public static bool ExecuteEssentialPrefix()
    {
        var initializationType = typeof(OneTimeInitialization);
        var stateField = initializationType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Static);
        var atlasField = initializationType.GetField("_atlasResourceLoader", BindingFlags.NonPublic | BindingFlags.Static);
        if (stateField == null || atlasField == null)
        {
            PatchHelper.Log("ModelDbInitPatch: OneTimeInitialization private fields not found; falling back to original ExecuteEssential().");
            return true;
        }

        var stateName = stateField.GetValue(null)?.ToString() ?? string.Empty;
        if (!string.Equals(stateName, "VeryEarly", StringComparison.Ordinal))
        {
            PatchHelper.Log($"ModelDbInitPatch: ExecuteEssential called in state {stateName}; falling back to original method.");
            return true;
        }

        PatchHelper.Log("Running patched OneTimeInitialization.ExecuteEssential with two-phase ModelDb init.");
        stateField.SetValue(null, Enum.Parse(stateField.FieldType, "Essential"));
        var atlasResourceLoader = new AtlasResourceLoader();
        atlasField.SetValue(null, atlasResourceLoader);
        ResourceLoader.AddResourceFormatLoader(atlasResourceLoader, true);
        AtlasManager.LoadEssentialAtlases();

        // BaseLib/RitsuLib both use ReflectionHelper.ModTypes during the
        // LocManager.Initialize and ModelDb.Init Harmony lifecycle. On Android the
        // cache can be populated too early while ModManager is still assigning
        // Mod.assembly, so clear it before each lifecycle point rather than only
        // inside our late ModelDb.Init prefix.
        RefreshModTypeCache("before LocManager.Initialize mod hooks");
        LocManager.Initialize();
        RefreshModTypeCache("before ModelDb.Init mod hooks");
        if (_modelDbInitPatched)
        {
            ModelDb.Init();
        }
        else
        {
            PatchHelper.Log("ModelDbInitPatch: ModelDb.Init replacement was not installed; running two-phase init directly as fallback. Mod ModelDb.Init hooks may not run.");
            RunTwoPhaseModelDbInit();
        }
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        return false;
    }

    public static bool InitPrefix()
    {
        RunTwoPhaseModelDbInit();
        return false;
    }

    public static void RunTwoPhaseModelDbInit()
    {
        PatchHelper.Log("Running patched ModelDb.Init().");
        RefreshModTypeCache("before ModelDb subtype scan");
        EnsureBaseLibCustomEnumsInitializedForMobile();

        var modelDbType = typeof(ModelDb);
        var allSubtypesProperty = modelDbType.GetProperty("AllAbstractModelSubtypes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var getIdMethod = modelDbType.GetMethod("GetId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Type) }, null);
        var contentByIdField = modelDbType.GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        var contentById = contentByIdField?.GetValue(null);
        var setItemMethod = contentById?.GetType().GetMethod("set_Item");

        if (allSubtypesProperty == null || getIdMethod == null || contentById == null || setItemMethod == null)
        {
            throw new InvalidOperationException("ModelDbInitPatch failed to locate ModelDb reflection members.");
        }

        var types = (Type[])allSubtypesProperty.GetValue(null) ?? Array.Empty<Type>();
        PatchHelper.Log($"ModelDbInitPatch phase 1: pre-registering {types.Length} model types.");

        var typeObjects = new Dictionary<Type, object>();
        var preRegistered = 0;
        for (var index = 0; index < types.Length; index++)
        {
            var type = types[index];
            try
            {
                var id = getIdMethod.Invoke(null, new object[] { type });
                var model = RuntimeHelpers.GetUninitializedObject(type);
                setItemMethod.Invoke(contentById, new[] { id, model });
                typeObjects[type] = model;
                preRegistered++;
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"ModelDbInitPatch phase 1 failed for {type.FullName}: {GetRootException(exception).GetType().Name}: {GetRootException(exception).Message}");
            }
        }
        PatchHelper.Log($"ModelDbInitPatch phase 1 complete: {preRegistered}/{types.Length} model types pre-registered.");

        var containsHarmony = new Harmony("com.wsdx233.sts2.android_port_compat.modeldb_contains");
        var containsMethod = modelDbType.GetMethod("Contains", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Type) }, null);
        var containsPrefix = typeof(ModelDbInitPatch).GetMethod(nameof(ContainsPrefix), BindingFlags.Public | BindingFlags.Static);
        var containsPatched = false;

        try
        {
            if (containsMethod != null && containsPrefix != null)
            {
                containsHarmony.Patch(containsMethod, prefix: new HarmonyMethod(containsPrefix));
                containsPatched = true;
            }
            else
            {
                PatchHelper.Log("ModelDbInitPatch: ModelDb.Contains(Type) not found; constructors may detect pre-registered placeholders.");
            }

            PatchHelper.Log("ModelDbInitPatch phase 2: running static and instance constructors.");
            _suppressContains = true;
            var successCount = 0;
            var failed = new List<Type>();

            foreach (var type in types)
            {
                if (!typeObjects.ContainsKey(type))
                    continue;

                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    constructor?.Invoke(typeObjects[type], null);
                    successCount++;
                }
                catch (Exception exception)
                {
                    var root = GetRootException(exception);
                    failed.Add(type);
                    PatchHelper.Log($"ModelDbInitPatch phase 2 failed for {type.FullName}: {root.GetType().Name}: {root.Message}");
                }
            }

            if (failed.Count > 0)
            {
                PatchHelper.Log($"ModelDbInitPatch warning: {failed.Count}/{types.Length} model constructors failed.");
            }
            else
            {
                PatchHelper.Log($"ModelDbInitPatch phase 2 complete: all {successCount} pre-registered model types initialized.");
            }
        }
        finally
        {
            _suppressContains = false;
            if (containsPatched && containsMethod != null && containsPrefix != null)
            {
                containsHarmony.Unpatch(containsMethod, containsPrefix);
            }
        }

    }

    private static void RefreshModTypeCache(string reason)
    {
        try
        {
            var modTypesField = typeof(ReflectionHelper).GetField("_modTypes", BindingFlags.NonPublic | BindingFlags.Static);
            if (modTypesField == null)
                return;
            modTypesField.SetValue(null, null);
            PatchHelper.Log($"ModelDbInitPatch: refreshed ReflectionHelper mod type cache {reason}.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: failed to refresh ReflectionHelper mod type cache: {exception.Message}");
        }
    }

    private static void EnsureBaseLibCustomEnumsInitializedForMobile()
    {
        try
        {
            var baseLibAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "BaseLib", StringComparison.Ordinal));
            if (baseLibAssembly == null)
                return;

            var customTargetType = baseLibAssembly.GetType("BaseLib.Patches.Features.CustomTargetType");
            if (customTargetType == null)
                return;

            var targetTypeFields = customTargetType
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(TargetType))
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .ToArray();
            if (targetTypeFields.Length == 0)
                return;

            var unsetFields = targetTypeFields
                .Where(field => field.GetValue(null) is TargetType targetType && targetType == TargetType.None)
                .ToArray();
            if (unsetFields.Length == 0)
                return;

            PatchHelper.Log($"ModelDbInitPatch: BaseLib CustomTargetType has {unsetFields.Length}/{targetTypeFields.Length} unset TargetType field(s) after ModelDb.Init prefixes; running mobile CustomEnum fallback.");
            RefreshModTypeCache("before BaseLib CustomEnum fallback");

            if (!TryRunBaseLibGenEnumValues(baseLibAssembly) || HasUnsetTargetTypeFields(targetTypeFields))
            {
                ForceBaseLibCustomTargetTypeFields(baseLibAssembly, targetTypeFields);
            }

            var stillUnset = targetTypeFields
                .Where(field => field.GetValue(null) is TargetType targetType && targetType == TargetType.None)
                .Select(field => field.Name)
                .ToArray();
            if (stillUnset.Length == 0)
            {
                PatchHelper.Log($"ModelDbInitPatch: BaseLib CustomTargetType fields initialized before target-type registry postfix ({targetTypeFields.Length} field(s)).");
            }
            else
            {
                PatchHelper.Log($"ModelDbInitPatch: WARNING BaseLib CustomTargetType fields still unset after fallback: {string.Join(", ", stillUnset)}");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: BaseLib CustomEnum fallback failed: {GetRootException(exception).GetType().Name}: {GetRootException(exception).Message}");
        }
    }

    private static bool TryRunBaseLibGenEnumValues(Assembly baseLibAssembly)
    {
        try
        {
            var genEnumValuesType = baseLibAssembly.GetType("BaseLib.Patches.Content.GenEnumValues");
            var findAndGenerate = genEnumValuesType?.GetMethod("FindAndGenerate", BindingFlags.NonPublic | BindingFlags.Static);
            if (findAndGenerate == null)
            {
                PatchHelper.Log("ModelDbInitPatch: BaseLib GenEnumValues.FindAndGenerate not found; using direct TargetType fallback.");
                return false;
            }

            findAndGenerate.Invoke(null, null);
            PatchHelper.Log("ModelDbInitPatch: invoked BaseLib GenEnumValues.FindAndGenerate fallback.");
            return true;
        }
        catch (Exception exception)
        {
            var root = GetRootException(exception);
            PatchHelper.Log($"ModelDbInitPatch: BaseLib GenEnumValues fallback failed: {root.GetType().Name}: {root.Message}; using direct TargetType fallback.");
            return false;
        }
    }

    private static bool HasUnsetTargetTypeFields(IEnumerable<FieldInfo> fields)
    {
        foreach (var field in fields)
        {
            if (field.GetValue(null) is TargetType targetType && targetType == TargetType.None)
                return true;
        }
        return false;
    }

    private static void ForceBaseLibCustomTargetTypeFields(Assembly baseLibAssembly, FieldInfo[] targetTypeFields)
    {
        var customEnumAttributeType = baseLibAssembly.GetType("BaseLib.Patches.Content.CustomEnumAttribute");
        var customEnumsType = baseLibAssembly.GetType("BaseLib.Patches.Content.CustomEnums");
        var generateKey = customEnumsType?.GetMethod("GenerateKey", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(FieldInfo) }, null);
        if (customEnumAttributeType == null || generateKey == null)
        {
            PatchHelper.Log("ModelDbInitPatch: direct BaseLib TargetType fallback unavailable; CustomEnumAttribute or GenerateKey missing.");
            return;
        }

        var forced = 0;
        foreach (var field in targetTypeFields)
        {
            if (!field.IsDefined(customEnumAttributeType, inherit: false))
                continue;
            if (field.GetValue(null) is TargetType current && current != TargetType.None)
                continue;

            var generated = generateKey.Invoke(null, new object[] { field });
            if (generated == null)
                continue;

            field.SetValue(null, generated);
            TryRecordBaseLibGeneratedEnumEntry(customEnumsType, field, generated);
            forced++;
        }

        PatchHelper.Log($"ModelDbInitPatch: direct BaseLib TargetType fallback initialized {forced} field(s).");
    }

    private static void TryRecordBaseLibGeneratedEnumEntry(Type customEnumsType, FieldInfo field, object generated)
    {
        try
        {
            var entriesField = customEnumsType.GetField("GeneratedCustomEnumEntries", BindingFlags.Public | BindingFlags.Static);
            if (entriesField?.GetValue(null) is not System.Collections.IDictionary outer)
                return;

            if (!outer.Contains(field.FieldType))
            {
                var innerType = entriesField.FieldType.GetGenericArguments()[1];
                outer[field.FieldType] = Activator.CreateInstance(innerType);
            }

            if (outer[field.FieldType] is not System.Collections.IDictionary inner)
                return;

            var key = Convert.ToInt32(generated);
            if (!inner.Contains(key))
            {
                inner[key] = ValueTuple.Create(GetBaseLibTypePrefix(field.DeclaringType), field.Name);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: failed to record BaseLib generated enum entry for {field.Name}: {exception.Message}");
        }
    }

    private static string GetBaseLibTypePrefix(Type type)
    {
        var namespaceName = type?.Namespace ?? string.Empty;
        var dotIndex = namespaceName.IndexOf('.');
        var rootNamespace = dotIndex < 0 ? namespaceName : namespaceName[..dotIndex];
        return rootNamespace.ToUpperInvariant() + "-";
    }

    private static Exception GetRootException(Exception exception)
    {
        while (exception.InnerException != null)
            exception = exception.InnerException;
        return exception;
    }
}
