namespace Fixture
{
    public static class Probe
    {
        public static bool EssentialReady;
        public static bool UiTypeInitialized;
        public static bool DependentModelInitialized;
        public static bool ModelTypeInitialized;
        public static int TargetFactoryReads;
        public static bool AssetSetsInitialized;
        public static bool PoolAssetCacheInitialized;
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.DailyRun
{
    public static class NDailyRunScreen
    {
        static NDailyRunScreen()
        {
            Fixture.Probe.UiTypeInitialized = true;
            if (!Fixture.Probe.EssentialReady)
                throw new System.InvalidOperationException("NDailyRunScreen initialized before essential startup");
        }

        public static int SetupLobbyParams(int value) => value;
        public static int AllKinds(int value) => value;
        public static int Prepared(int value) => value;
        public static int Skipped(int value) => value;
        public static int Failing(int value) => value;
        public static int Direct(int value) => value;
    }
}

namespace MegaCrit.Sts2.Core.Models
{
    public static class SyntheticModel
    {
        static SyntheticModel() => Fixture.Probe.ModelTypeInitialized = true;
        public static int Register(int value) => value;
    }

    public abstract class AbstractModel { }

    public static class ModelDb
    {
        public static System.Collections.Generic.IEnumerable<AbstractModel> AllCards
        {
            get
            {
                Fixture.Probe.TargetFactoryReads++;
                if (!Fixture.Probe.EssentialReady)
                    throw new System.Collections.Generic.KeyNotFoundException("ModelDb.AllCards read before initialization");
                return new AbstractModel[] { new OtherModel() };
            }
        }
        public static string GetId(System.Type type) => type.Name;
        public static T Get<T>() where T : AbstractModel, new()
        {
            if (!Fixture.Probe.EssentialReady)
                throw new System.Collections.Generic.KeyNotFoundException("canonical ModelDb is not initialized");
            return new T();
        }
    }

    public sealed class DependentModel : AbstractModel
    {
        private static readonly AbstractModel Other = ModelDb.Get<OtherModel>();

        static DependentModel() => Fixture.Probe.DependentModelInitialized = Other != null;

        public static int Register(int value) => value;
    }

    public sealed class OtherModel : AbstractModel
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int FromFactory(int value) => value;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int FromIterator(int value) => value;
    }
}

namespace MegaCrit.Sts2.Core.Localization
{
    public static class LocManager
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Initialize() { }
    }
}

namespace MegaCrit.Sts2.Core.Modding
{
    public interface IPoolModel { }

    public static class ModHelper
    {
        private sealed class PoolContent
        {
            public bool Frozen;
            public readonly System.Collections.Generic.List<System.Type> Types = new();
        }

        private static readonly System.Collections.Generic.Dictionary<System.Type, PoolContent> Content = new();

        public static void AddModelToPool(System.Type poolType, System.Type modelType)
        {
            if (!Content.TryGetValue(poolType, out var entry))
                Content.Add(poolType, entry = new PoolContent());
            if (entry.Frozen)
                throw new System.InvalidOperationException("pool content has already been consumed");
            entry.Types.Add(modelType);
        }

        public static System.Collections.Generic.IEnumerable<T> ConcatModelsFromMods<T>(IPoolModel pool,
            System.Collections.Generic.IEnumerable<T> vanilla) where T : MegaCrit.Sts2.Core.Models.AbstractModel
        {
            if (!Content.TryGetValue(pool.GetType(), out var entry))
                Content.Add(pool.GetType(), entry = new PoolContent());
            entry.Frozen = true;
            return System.Linq.Enumerable.Concat(vanilla,
                System.Linq.Enumerable.Select(entry.Types, type => (T)System.Activator.CreateInstance(type)));
        }
    }

    public sealed class ResourcePool : IPoolModel { }
    public sealed class RegisteredResourceModel : MegaCrit.Sts2.Core.Models.AbstractModel { }
    public sealed class LaterResourceModel : MegaCrit.Sts2.Core.Models.AbstractModel { }

    public static class RegistrationStatics
    {
        private static readonly string Id = MegaCrit.Sts2.Core.Models.ModelDb.GetId(typeof(ResourcePool));
        public static string Register() => Id;
    }
}

namespace MegaCrit.Sts2.Core.Assets
{
    public static class AssetSets
    {
        private static readonly string[] Common;
        static AssetSets()
        {
            Common = System.Linq.Enumerable.ToArray(AssetSetSources.GetCommon());
            Fixture.Probe.AssetSetsInitialized = true;
        }
        public static System.Collections.Generic.IReadOnlyList<string> CommonAssets => Common;
    }

    internal static class AssetSetSources
    {
        internal static System.Collections.Generic.IEnumerable<string> GetCommon()
        {
            yield return MegaCrit.Sts2.Core.Models.ModelDb.Get<MegaCrit.Sts2.Core.Models.OtherModel>().GetType().Name;
        }
    }

    public static class PoolAssetCache
    {
        private static readonly string[] Paths;
        static PoolAssetCache()
        {
            Paths = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
                MegaCrit.Sts2.Core.Modding.ModHelper.ConcatModelsFromMods<MegaCrit.Sts2.Core.Models.AbstractModel>(
                    new MegaCrit.Sts2.Core.Modding.ResourcePool(), System.Array.Empty<MegaCrit.Sts2.Core.Models.AbstractModel>()),
                model => model.GetType().Name));
            Fixture.Probe.PoolAssetCacheInitialized = true;
        }
        public static System.Collections.Generic.IReadOnlyList<string> GetPaths() => Paths;
        public static int Direct(int value) => value;
    }
}
