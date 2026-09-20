namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    public sealed class DamageResult
    {
        public int UnblockedDamage { get; init; }
    }
}

namespace SyntheticGame
{
    public sealed class ClassCarrier
    {
        public int Value { get; init; }
    }

    public struct StructCarrier
    {
        public int Value { get; init; }
    }

    public sealed class GenericCarrier<T>
    {
        public T Value { get; init; }
    }
}
