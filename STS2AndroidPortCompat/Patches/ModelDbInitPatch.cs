using System;
using System.Collections;
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
/// Replaces the original one-pass ModelDb.Init with a two-phase initialization
/// that matches PC ordering and behavior as closely as possible.
///
/// Why a two-phase init is needed at all:
/// Some vanilla models construct other models from their static field
/// initializers, e.g. <c>BowlbugsNormal.._workerValidCounts</c> calls
/// <c>ModelDb.Monster&lt;BowlbugEgg&gt;()</c> in its static constructor. PC's
/// CoreCLR happens to tolerate this ordering, but Android/Mono triggers the
/// static constructor eagerly and throws KeyNotFound when the referenced model
/// is not registered yet. Pre-registering uninitialized placeholders for every
/// model first, then running the constructors in place, lets those cross-model
/// references resolve regardless of construction order.
///
/// Critical ordering invariant (this is what previous fixes got wrong):
/// Mods such as YuWanCard/BaseLib postfix <c>ModelDb.GetEntry</c> to add a
/// namespace prefix to their content ids (e.g. ENCOUNTER.YUWANCARD-KILLER_ELITE)
/// and permanently cache the first GetEntry result per type. On PC, every mod's
/// Harmony PatchAll runs during ModManager.Initialize (ExecuteVeryEarly), which
/// is strictly before ModelDb.Init (ExecuteEssential). So by the time any id is
/// computed, the prefix patches are already installed. We therefore must NOT
/// call ModelDb.GetId/GetEntry for any model type before mod patches are applied.
/// All id-dependent work happens inside the patched ModelDb.Init, which runs at
/// the same point in the lifecycle as on PC.
/// </summary>
public static class ModelDbInitPatch
{
    private static bool _suppressContains;
    private static bool _modelDbInitPatched;
    private static readonly object _phaseLock = new();
    private static readonly Dictionary<Type, object> _preRegisteredTypeObjects = new();
    private static readonly List<Type> _preRegisteredTypeOrder = new();
    private static bool _phase2Completed;
    private static FieldInfo _modelIdBackingField;
    private static bool _loggedModelIdSeedFailure;
    private static bool _containsPatched;
    private static int _modInitializationContainsShieldDepth;
    private static readonly Dictionary<ModelId, Type> _preRegisteredPlaceholderOwnersById = new();
    private static readonly HashSet<Type> _loggedModInitializationContainsShieldTypes = new();

    public static void Apply(Harmony harmony)
    {
        try
        {
            var initTarget = typeof(ModelDb).GetMethod(nameof(ModelDb.Init), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var initPrefix = typeof(ModelDbInitPatch).GetMethod(nameof(InitPrefix), BindingFlags.Public | BindingFlags.Static);
            var initPostfix = typeof(ModelDbInitPatch).GetMethod(nameof(InitPostfix), BindingFlags.Public | BindingFlags.Static);
            if (initTarget == null || initPrefix == null || initPostfix == null)
            {
                PatchHelper.Log("FAILED ModelDb.Init: method not found");
            }
            else
            {
                // Normal path: Priority.Last prefix lets user/BaseLib/YuWanCard
                // ModelDb.Init prefixes run first (matching PC), then runs our
                // constructor phase and returns false to skip vanilla one-pass init.
                //
                // Android/MOD caveat: some MOD prefixes return false themselves.
                // Harmony can then skip later prefixes, including ours. A
                // Priority.First postfix is therefore also installed as a safety net:
                // postfixes still run when a prefix skips the original, and a high
                // priority postfix runs before normal MOD postfixes. RunTwoPhase is
                // idempotent, so either path is safe.
                harmony.Patch(
                    initTarget,
                    prefix: new HarmonyMethod(initPrefix) { priority = Priority.Last },
                    postfix: new HarmonyMethod(initPostfix) { priority = Priority.First });
                _modelDbInitPatched = true;
                PatchHelper.Log("Patched ModelDb.Init (two-phase placeholder + constructor init; preserves mod ModelDb.Init prefix/postfix hooks, with postfix safety net).");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED ModelDb.Init: {exception}");
        }

        PatchContains(harmony);

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
            PatchHelper.Log("Patched OneTimeInitialization.ExecuteEssential (mirrors PC LocManager/ModelDb/ModelIdSerializationCache ordering).");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED OneTimeInitialization.ExecuteEssential: {exception}");
        }
    }

    public static void PatchContains(Harmony harmony)
    {
        if (_containsPatched)
            return;

        try
        {
            var containsMethod = typeof(ModelDb).GetMethod("Contains", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Type) }, null);
            var containsPrefix = typeof(ModelDbInitPatch).GetMethod(nameof(ContainsPrefix), BindingFlags.Public | BindingFlags.Static);
            if (containsMethod == null || containsPrefix == null)
            {
                PatchHelper.Log("ModelDbInitPatch: ModelDb.Contains(Type) not found; constructors may detect pre-registered placeholders.");
                return;
            }

            harmony.Patch(containsMethod, prefix: new HarmonyMethod(containsPrefix));
            _containsPatched = true;
            PatchHelper.Log("Patched ModelDb.Contains(Type) for Android constructor-phase and mod-initializer placeholder safety.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED ModelDb.Contains(Type): {exception}");
        }
    }

    public static bool ContainsPrefix(Type __0, ref bool __result)
    {
        var type = __0;
        // During the constructor phase the placeholder objects are already present
        // in _contentById. Vanilla AbstractModel..ctor throws DuplicateModelException
        // if ModelDb.Contains(type) is true, so we suppress Contains while running
        // constructors in place over the existing placeholders.
        if (_suppressContains)
        {
            __result = false;
            return false;
        }

        // During user MOD initializers, Android's early vanilla placeholder pass is
        // the main intentional PC-ordering difference. A MOD may construct a
        // temporary/canonical-looking model object before ModelDb.Init; on PC that
        // sees an empty ModelDb and succeeds, but on Android ModelDb.Contains(type)
        // can hit an early vanilla placeholder with the same derived id (for
        // example a MOD card class named Taunt -> CARD.TAUNT). Hide only those
        // early placeholder hits for non-game-assembly model types. Exact already
        // tracked types and post-ModelDb behavior are left unchanged.
        if (ShouldHideEarlyPlaceholderFromModInitializer(type))
        {
            __result = false;
            return false;
        }

        return true;
    }

    private static bool ShouldHideEarlyPlaceholderFromModInitializer(Type type)
    {
        lock (_phaseLock)
        {
            if (_modInitializationContainsShieldDepth <= 0 || _phase2Completed)
                return false;
        }

        if (type == null || !type.IsSubclassOf(typeof(AbstractModel)))
            return false;

        // Never hide vanilla/game assembly checks: those early placeholders are
        // deliberately visible to keep Android/Mono cctor-triggered vanilla lookups
        // such as UnlockState -> ModelDb.AllEncounters safe.
        if (type.Assembly == typeof(AbstractModel).Assembly)
            return false;

        ModelId id;
        try
        {
            id = ModelDb.GetId(type);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: mod-initializer Contains shield could not compute id for {type.FullName}: {GetRootException(exception).Message}");
            return false;
        }

        Type placeholderOwner;
        lock (_phaseLock)
        {
            if (!_preRegisteredPlaceholderOwnersById.TryGetValue(id, out placeholderOwner))
                return false;
            if (placeholderOwner == type)
                return false;
        }

        // The shield is intentionally narrow: only early vanilla placeholders are
        // hidden from MOD types. Once MOD placeholders are registered in phase 1,
        // the initializer shield has already ended, and real duplicate checks stay
        // visible.
        if (placeholderOwner?.Assembly != typeof(AbstractModel).Assembly)
            return false;

        lock (_phaseLock)
        {
            if (_loggedModInitializationContainsShieldTypes.Add(type))
            {
                PatchHelper.Log(
                    $"ModelDbInitPatch: hiding early vanilla placeholder Contains hit during MOD initialization for {type.FullName} (id={id}, placeholder={placeholderOwner.FullName}).");
            }
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

        PatchHelper.Log("Running patched OneTimeInitialization.ExecuteEssential.");
        stateField.SetValue(null, Enum.Parse(stateField.FieldType, "Essential"));
        var atlasResourceLoader = new AtlasResourceLoader();
        atlasField.SetValue(null, atlasResourceLoader);
        ResourceLoader.AddResourceFormatLoader(atlasResourceLoader, true);
        AtlasManager.LoadEssentialAtlases();

        // Register VANILLA model placeholders (idempotent). This normally already
        // ran in ModLoaderPatches before mods loaded; repeat here as a safety net in
        // case mod loading took a different path. Vanilla ids never trigger a mod
        // GetEntry prefix patch, so this cannot poison a mod's permanent id cache.
        // Mod model placeholders are still deferred to ModelDb.Init (phase 1),
        // where the prefix patches are already applied (matching PC).
        EnsureVanillaModelPlaceholdersPreRegistered();

        // BaseLib/RitsuLib use ReflectionHelper.ModTypes inside their
        // LocManager.Initialize / ModelDb.Init Harmony hooks. On Android the cache
        // can be populated too early while ModManager is still assigning
        // Mod.assembly, so refresh it right before each lifecycle point.
        RefreshModTypeCache("before LocManager.Initialize mod hooks");
        LocManager.Initialize();
        RefreshModTypeCache("before ModelDb.Init mod hooks");

        // Register the FULL placeholder set (vanilla + mod types) BEFORE calling
        // ModelDb.Init. On Android/Mono, mod ModelDb.Init prefixes (which run before
        // our Priority.Last InitPrefix) can eagerly trigger model static
        // constructors that reference other models, e.g.
        // wuwancients.HiddenSeaRecord..cctor builds a static map referencing
        // ModelDb.Relic<LongSnakeNecklace>() (a mod relic). All mod assemblies are
        // loaded and their GetEntry prefix patches applied at this point, so ids are
        // final (matching PC). Phase 2 (constructors) still runs inside InitPrefix.
        RunPhase1PreRegistration();

        // ModelDb.Init is patched to run phase 2. If for some reason the patch did
        // not install, run the two-phase init directly as a fallback.
        if (_modelDbInitPatched)
        {
            ModelDb.Init();
            // Fallback in case Harmony prefix/postfix ordering is changed by a MOD
            // in a way that skipped our hooks. Idempotent when the normal path ran.
            RunTwoPhaseModelDbInit();
        }
        else
        {
            PatchHelper.Log("ModelDbInitPatch: ModelDb.Init replacement not installed; running two-phase init directly (mod ModelDb.Init hooks may not run).");
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

    public static void InitPostfix()
    {
        RunTwoPhaseModelDbInit();
    }

    public static void RunTwoPhaseModelDbInit()
    {
        lock (_phaseLock)
        {
            if (_phase2Completed)
            {
                PatchHelper.Log("ModelDbInitPatch: ModelDb.Init invoked again after completion; ignoring (already initialized).");
                return;
            }
        }

        // Phase 1 may already have run from ExecuteEssentialPrefix (the normal
        // path). RunPhase1PreRegistration is idempotent.
        RunPhase1PreRegistration();

        // BaseLib's own ModelDb.Init prefixes have now had a chance to initialize
        // custom enum fields. Run the mobile fallback only after those prefixes,
        // as in the previous two-phase flow.
        EnsureBaseLibCustomEnumsInitializedForMobile();

        // Phase 2: run the real static + instance constructors in place over the
        // placeholders.
        RunPreRegisteredModelConstructors();
    }

    private static bool _phase1Completed;

    /// <summary>
    /// Phase 1: register an uninitialized placeholder for every model type using its
    /// FINAL id (mod GetEntry prefix patches are already applied at this point,
    /// exactly like PC). This lets cross-model static-field references resolve
    /// during the constructor phase regardless of order. Idempotent.
    /// </summary>
    public static void RunPhase1PreRegistration()
    {
        lock (_phaseLock)
        {
            if (_phase1Completed || _phase2Completed)
                return;
            _phase1Completed = true;
        }

        PatchHelper.Log("Running patched ModelDb.Init() phase 1 pre-registration.");

        // Vanilla placeholders may already be registered from the pre-mod-load pass;
        // PreRegisterModelPlaceholders skips anything already tracked.
        PreRegisterModelPlaceholders(GetAllModelTypes(), "phase 1 (all model types)");
    }

    /// <summary>
    /// Temporarily hides early vanilla placeholder duplicate hits from user MOD
    /// model constructors while ModManager calls MOD initializers. This restores PC
    /// behavior only for this narrow window; Android still exposes vanilla
    /// placeholders to vanilla early reads and uses normal duplicate checks after
    /// ModelDb.Init starts.
    /// </summary>
    public static IDisposable BeginModInitializationContainsShield()
    {
        lock (_phaseLock)
        {
            _modInitializationContainsShieldDepth++;
        }
        return ModInitializationContainsShieldScope.Instance;
    }

    /// <summary>
    /// Pre-registers placeholders for VANILLA model types only. Safe to call before
    /// mod GetEntry prefix patches matter, because vanilla types are never prefixed.
    /// Idempotent: already-tracked types are skipped, so calling it both before mod
    /// loading (from ModLoaderPatches) and again before LocManager is cheap.
    /// </summary>
    public static void EnsureVanillaModelPlaceholdersPreRegistered()
    {
        lock (_phaseLock)
        {
            if (_phase2Completed)
                return;
        }

        Type[] vanillaTypes;
        try
        {
            vanillaTypes = GetVanillaModelTypes();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: failed to enumerate vanilla model types for early pre-registration: {GetRootException(exception).Message}");
            return;
        }

        PreRegisterModelPlaceholders(vanillaTypes, "early vanilla pre-registration (before LocManager mod hooks)");
    }

    private static Type[] GetVanillaModelTypes()
    {
        var subtypesType = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.AbstractModelSubtypes");
        var allProperty = subtypesType?.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (allProperty?.GetValue(null) is not IEnumerable<Type> types)
            throw new InvalidOperationException("ModelDbInitPatch failed to locate AbstractModelSubtypes.All.");
        return types.ToArray();
    }

    private static Type[] GetAllModelTypes()
    {
        var allSubtypesProperty = typeof(ModelDb).GetProperty("AllAbstractModelSubtypes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (allSubtypesProperty == null)
            throw new InvalidOperationException("ModelDbInitPatch failed to locate ModelDb.AllAbstractModelSubtypes.");
        return (Type[])allSubtypesProperty.GetValue(null) ?? Array.Empty<Type>();
    }

    private static void PreRegisterModelPlaceholders(Type[] rawTypes, string phaseLabel)
    {
        var types = (rawTypes ?? Array.Empty<Type>())
            .Where(type => type != null && !type.IsAbstract && typeof(AbstractModel).IsAssignableFrom(type))
            .ToArray();
        if (types.Length == 0)
            return;

        var modelDbType = typeof(ModelDb);
        var getIdMethod = modelDbType.GetMethod("GetId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Type) }, null);
        var contentByIdField = modelDbType.GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        var contentById = contentByIdField?.GetValue(null);
        var setItemMethod = contentById?.GetType().GetMethod("set_Item");
        var dictionary = contentById as IDictionary;

        if (getIdMethod == null || contentById == null || setItemMethod == null || dictionary == null)
        {
            PatchHelper.Log($"ModelDbInitPatch {phaseLabel} failed: could not locate ModelDb reflection members.");
            return;
        }

        var preRegistered = 0;
        lock (_phaseLock)
        {
            foreach (var type in types)
            {
                if (_preRegisteredTypeObjects.ContainsKey(type))
                    continue;

                try
                {
                    // GetId -> GetEntry runs any applicable mod prefix patches. For
                    // vanilla types this is a no-op (no prefix); for the phase 1
                    // (post mod-load) call the prefix patches are already applied,
                    // producing the final (prefixed) id, exactly like PC.
                    var id = getIdMethod.Invoke(null, new object[] { type });
                    if (dictionary.Contains(id))
                        continue;

                    var model = RuntimeHelpers.GetUninitializedObject(type);
                    TrySeedModelId(model, id);
                    setItemMethod.Invoke(contentById, new[] { id, model });
                    _preRegisteredTypeObjects[type] = model;
                    _preRegisteredTypeOrder.Add(type);
                    if (id is ModelId modelId)
                        _preRegisteredPlaceholderOwnersById[modelId] = type;
                    preRegistered++;
                }
                catch (Exception exception)
                {
                    var root = GetRootException(exception);
                    PatchHelper.Log($"ModelDbInitPatch {phaseLabel} failed for {type.FullName}: {root.GetType().Name}: {root.Message}");
                }
            }
        }

        PatchHelper.Log($"ModelDbInitPatch {phaseLabel}: pre-registered {preRegistered} model placeholder(s); total tracked={_preRegisteredTypeObjects.Count}.");
    }

    private static void TrySeedModelId(object model, object id)
    {
        try
        {
            _modelIdBackingField ??= typeof(AbstractModel).GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            _modelIdBackingField?.SetValue(model, id);
        }
        catch (Exception exception)
        {
            if (!_loggedModelIdSeedFailure)
            {
                _loggedModelIdSeedFailure = true;
                PatchHelper.Log($"ModelDbInitPatch: failed to seed placeholder model id; early ModelDb readers may see default ids: {exception.Message}");
            }
        }
    }

    private static void RunPreRegisteredModelConstructors()
    {
        List<Type> types;
        lock (_phaseLock)
        {
            types = new List<Type>(_preRegisteredTypeOrder);
        }

        // Placeholder enumeration before construction can prime ModelDb-level and
        // per-model derived caches with incomplete data; clear them around the
        // constructor phase.
        ClearPreRegisteredModelInstanceCaches("before constructor phase");
        ClearModelDbDerivedCaches("before constructor phase");

        try
        {
            PatchHelper.Log($"ModelDbInitPatch phase 2: running static and instance constructors for {types.Count} pre-registered model types.");
            _suppressContains = true;
            var successCount = 0;
            var failed = new List<Type>();

            foreach (var type in types)
            {
                object model;
                lock (_phaseLock)
                {
                    if (!_preRegisteredTypeObjects.TryGetValue(type, out model))
                        continue;
                }

                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    constructor?.Invoke(model, null);
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
                PatchHelper.Log($"ModelDbInitPatch warning: {failed.Count}/{types.Count} model constructors failed.");
            }
            else
            {
                PatchHelper.Log($"ModelDbInitPatch phase 2 complete: all {successCount} pre-registered model types initialized.");
            }
        }
        finally
        {
            _suppressContains = false;
            ClearPreRegisteredModelInstanceCaches("after constructor phase");
            ClearModelDbDerivedCaches("after constructor phase");
            lock (_phaseLock)
            {
                _phase2Completed = true;
            }
        }
    }

    private static void ClearPreRegisteredModelInstanceCaches(string reason)
    {
        try
        {
            List<object> models;
            lock (_phaseLock)
            {
                models = _preRegisteredTypeObjects.Values.ToList();
            }

            var cleared = 0;
            foreach (var model in models)
            {
                if (model == null)
                    continue;

                for (var type = model.GetType(); type != null && type != typeof(object); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        if (!field.Name.StartsWith("_all", StringComparison.Ordinal))
                            continue;
                        if (field.FieldType.IsValueType)
                            continue;
                        if (field.GetValue(model) == null)
                            continue;
                        field.SetValue(model, null);
                        cleared++;
                    }
                }
            }

            if (cleared > 0)
                PatchHelper.Log($"ModelDbInitPatch: cleared {cleared} per-model derived cache field(s) {reason}.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: failed to clear per-model caches {reason}: {exception.Message}");
        }
    }

    private static void ClearModelDbDerivedCaches(string reason)
    {
        try
        {
            string[] cacheFields =
            {
                "_allCards",
                "_allCardPools",
                "_allCharacterCardPools",
                "_allSharedEvents",
                "_allEvents",
                "_allEncounters",
                "_allPotions",
                "_allPotionPools",
                "_allCharacterPotionPools",
                "_allSharedPotionPools",
                "_allPowers",
                "_allRelics",
                "_allCharacterRelicPools",
                "_badges",
                "_achievements"
            };

            var modelDbType = typeof(ModelDb);
            foreach (var fieldName in cacheFields)
            {
                var field = modelDbType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
                field?.SetValue(null, null);
            }
            PatchHelper.Log($"ModelDbInitPatch: cleared ModelDb derived caches {reason}.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ModelDbInitPatch: failed to clear derived caches {reason}: {exception.Message}");
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

    private sealed class ModInitializationContainsShieldScope : IDisposable
    {
        public static readonly ModInitializationContainsShieldScope Instance = new();

        private ModInitializationContainsShieldScope()
        {
        }

        public void Dispose()
        {
            lock (_phaseLock)
            {
                if (_modInitializationContainsShieldDepth > 0)
                    _modInitializationContainsShieldDepth--;
            }
        }
    }

    private static Exception GetRootException(Exception exception)
    {
        while (exception.InnerException != null)
            exception = exception.InnerException;
        return exception;
    }
}
