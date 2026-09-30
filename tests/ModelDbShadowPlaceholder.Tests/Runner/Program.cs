using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2Mobile.Patches;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static int ContentCount()
{
    var field = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
    Assert(field != null, "ModelDb._contentById was not found");
    return ((IDictionary)field.GetValue(null)).Count;
}
static object Invoke(MethodInfo method, params object[] arguments)
{
    try { return method.Invoke(null, arguments); }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        throw;
    }
}

static void ExpectFailure<TException>(Action action, string description) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(description);
}


var before = ContentCount();
ModelDbInitPatch.Apply(new Harmony("sts2mobile.modeldb-shadow-tests"));
ModelDbInitPatch.EnsureVanillaModelPlaceholdersPreRegistered();
var duringModInitialization = ContentCount();

var subtypesType = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.AbstractModelSubtypes");
var allProperty = subtypesType?.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
var vanillaTypes = ((IEnumerable<Type>)allProperty.GetValue(null)).Where(type => !type.IsAbstract).ToArray();
var modelTypes = new[]
{
    vanillaTypes.Single(type => type.Name == "Overgrowth"),
    vanillaTypes.Single(type => type.Name == "Astrolabe"),
    vanillaTypes.First(type => typeof(CardModel).IsAssignableFrom(type)),
    vanillaTypes.First(type => typeof(CardPoolModel).IsAssignableFrom(type))
};
var methods = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
var openGet = methods.Single(method => method.Name == "Get" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
var getByType = methods.Single(method => method.Name == "Get" && !method.IsGenericMethod && method.GetParameters().Length == 1);
var openGetById = methods.Single(method => method.Name == "GetById" && method.IsGenericMethodDefinition);
var openGetByIdOrNull = methods.Single(method => method.Name == "GetByIdOrNull" && method.IsGenericMethodDefinition);
var content = (IDictionary)typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
var shadows = new Dictionary<Type, object>();
foreach (var type in modelTypes)
{
    var id = ModelDb.GetId(type);
    var shadow = Invoke(openGetById.MakeGenericMethod(type), id);
    Assert(shadow?.GetType() == type, $"early GetById<{type.Name}> returned the wrong model");
    Assert(ReferenceEquals(Invoke(openGetByIdOrNull.MakeGenericMethod(type), id), shadow), $"early nullable lookup changed {type.Name} identity");
    Assert(ReferenceEquals(Invoke(openGet.MakeGenericMethod(type)), shadow), $"early Get<{type.Name}> did not resolve shadow");
    Assert(ReferenceEquals(Invoke(getByType, type), shadow), $"early Get(Type) did not resolve {type.Name} shadow");
    var category = type;
    while (category.BaseType != typeof(AbstractModel)) category = category.BaseType;
    var typedGetter = methods.Single(method => method.Name == category.Name.Replace("Model", "", StringComparison.Ordinal)
        && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
    Assert(ReferenceEquals(Invoke(typedGetter.MakeGenericMethod(type)), shadow), $"early typed {type.Name} getter did not resolve shadow");
    Assert(ContentCount() == before, $"early {type.Name} reads populated canonical ModelDb");
    shadows.Add(type, shadow);
}

var firstType = modelTypes[0];
var firstId = ModelDb.GetId(firstType);
var canonical = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(firstType);
content[firstId] = canonical;
Assert(ReferenceEquals(Invoke(openGet.MakeGenericMethod(firstType)), canonical), "Get<T> did not prefer real canonical content over shadow");
Assert(ReferenceEquals(Invoke(getByType, firstType), canonical), "Get(Type) did not prefer real canonical content over shadow");
Assert(ReferenceEquals(Invoke(openGetById.MakeGenericMethod(firstType), firstId), canonical), "GetById did not prefer canonical content over shadow");
Assert(ReferenceEquals(Invoke(openGetByIdOrNull.MakeGenericMethod(firstType), firstId), canonical), "nullable lookup did not prefer canonical content over shadow");
content.Remove(firstId);

var independentContent = new Dictionary<ModelId, AbstractModel>();
Assert(!independentContent.TryGetValue(firstId, out var absent) && absent == null,
    "shadow access leaked into a non-canonical model dictionary");
ExpectFailure<KeyNotFoundException>(() => _ = independentContent[firstId],
    "non-canonical model dictionary indexer exposed a shadow");
var unrelatedModels = new Dictionary<ModelId, string> { [firstId] = "unrelated" };
Assert(unrelatedModels.TryGetValue(firstId, out var label) && label == "unrelated"
    && unrelatedModels[firstId] == "unrelated", "shared dictionary code changed a different value type");
var unrelatedKeys = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["key"] = firstId };
Assert(unrelatedKeys.TryGetValue("KEY", out var value) && ReferenceEquals(value, firstId)
    && ReferenceEquals(unrelatedKeys["KEY"], firstId), "shared dictionary code changed a different key type or comparer");
ExpectFailure<ArgumentNullException>(() => unrelatedKeys.TryGetValue(null, out _), "dictionary null-key errors changed");
ExpectFailure<ArgumentNullException>(() => Invoke(openGetByIdOrNull.MakeGenericMethod(firstType), new object[] { null }),
    "nullable ModelDb lookup swallowed a null-key error");

var missingId = new ModelId("ACT", "SHADOW_TEST_MISSING");
Assert(Invoke(openGetByIdOrNull.MakeGenericMethod(firstType), missingId) == null, "missing nullable lookup did not remain null");
ExpectFailure<MegaCrit.Sts2.Core.Models.Exceptions.ModelNotFoundException>(
    () => Invoke(openGetById.MakeGenericMethod(firstType), missingId), "missing GetById did not retain ModelNotFoundException");
ExpectFailure<InvalidCastException>(
    () => Invoke(openGetById.MakeGenericMethod(modelTypes[1]), firstId), "wrong model category did not retain InvalidCastException");
ExpectFailure<InvalidOperationException>(() => Invoke(getByType, typeof(string)), "Get(Type) accepted a non-model type");
var init = methods.Single(method => method.Name == "Init");
if (init.GetParameters().Length != 0)
    Invoke(init, (object)Array.Empty<Type>());

Assert(duringModInitialization == before && ContentCount() == before, "canonical ModelDb content changed during MOD initialization window");
ModelDbInitPatch.PublishShadowPlaceholders();
var afterPublish = ContentCount();
Assert(afterPublish == vanillaTypes.Length, "phase 1 did not publish the vanilla model set");
foreach (var type in modelTypes)
{
    Assert(ReferenceEquals(Invoke(openGet.MakeGenericMethod(type)), shadows[type]), $"published Get<{type.Name}> changed object identity");
    Assert(ReferenceEquals(Invoke(getByType, type), shadows[type]), $"published Get(Type) changed {type.Name} identity");
}
content.Remove(firstId);
ExpectFailure<KeyNotFoundException>(() => Invoke(openGet.MakeGenericMethod(firstType)), "missing Get<T> did not retain KeyNotFoundException after publication");
ExpectFailure<MegaCrit.Sts2.Core.Models.Exceptions.ModelNotFoundException>(() => Invoke(getByType, firstType), "missing Get(Type) did not retain ModelNotFoundException after publication");
Assert(Invoke(openGetByIdOrNull.MakeGenericMethod(firstType), firstId) == null, "published lookup retained a stale shadow");
Console.WriteLine($"ModelDb shadow lookup regression passed: early/published={string.Join(",", modelTypes.Select(type => type.Name))} canonical_before={before} canonical_after_publish={afterPublish}");
