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

var before = ContentCount();
ModelDbInitPatch.Apply(new Harmony("sts2mobile.modeldb-shadow-tests"));
ModelDbInitPatch.EnsureVanillaModelPlaceholdersPreRegistered();
var duringModInitialization = ContentCount();

var subtypesType = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.AbstractModelSubtypes");
var allProperty = subtypesType?.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
var modelType = ((IEnumerable<Type>)allProperty?.GetValue(null)).First(type => !type.IsAbstract);
var modelId = ModelDb.GetId(modelType);
var getById = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .Single(method => method.Name == "GetById" && method.IsGenericMethodDefinition)
    .MakeGenericMethod(modelType);
var getByIdOrNull = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .Single(method => method.Name == "GetByIdOrNull" && method.IsGenericMethodDefinition)
    .MakeGenericMethod(modelType);
var shadowModel = getById.Invoke(null, new object[] { modelId });
var shadowModelOrNull = getByIdOrNull.Invoke(null, new object[] { modelId });

Assert(duringModInitialization == before, "canonical ModelDb content changed during MOD initialization window");
Assert(shadowModel?.GetType() == modelType, "GetById did not resolve the shadow placeholder");
Assert(shadowModelOrNull?.GetType() == modelType, "GetByIdOrNull did not resolve the shadow placeholder");

ModelDbInitPatch.PublishShadowPlaceholders();
var afterPublish = ContentCount();
Assert(afterPublish > duringModInitialization, "phase 1 did not publish shadow placeholders");
Console.WriteLine($"before={before} during_mod_initialization={duringModInitialization} after_phase1_publish={afterPublish} model={modelType.FullName}");
