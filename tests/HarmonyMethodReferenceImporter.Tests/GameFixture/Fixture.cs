namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    public sealed class DamageResult
    {
        // JSON must trigger installation even when the game probe has no modreq.
        public int UnblockedDamage { get; set; }
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
