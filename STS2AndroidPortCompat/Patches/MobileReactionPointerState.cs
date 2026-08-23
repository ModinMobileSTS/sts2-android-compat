namespace STS2Mobile.Patches;

internal sealed class MobileReactionPointerState
{
    internal const int NoPointerId = -2;

    internal int ActivePointerId { get; private set; } = NoPointerId;

    internal bool HasActivePointer => ActivePointerId != NoPointerId;

    internal bool HasMovedSincePress { get; private set; }

    internal bool TryBegin(int pointerId)
    {
        if (HasActivePointer)
            return false;

        ActivePointerId = pointerId;
        HasMovedSincePress = false;
        return true;
    }

    internal bool ShouldUpdateSelection(float distanceSquared, float movementThresholdSquared)
    {
        if (!HasActivePointer)
            return false;
        if (!HasMovedSincePress && distanceSquared <= movementThresholdSquared)
            return false;

        HasMovedSincePress = true;
        return true;
    }

    internal void Cancel()
    {
        ActivePointerId = NoPointerId;
        HasMovedSincePress = false;
    }
}
