using System.Numerics;

namespace STS2Mobile.Patches;

internal static class MobileReactionWheelPlacement
{
    public static Vector2 GetTransformedCenter(Matrix3x2 localToTarget, Vector2 size)
    {
        return Vector2.Transform(size * 0.5f, localToTarget);
    }
    public static Vector2 GetAnchoredPosition(
        Vector2 capturedPosition,
        Vector2 capturedParentSize,
        Vector2 currentParentSize,
        Vector2 anchor)
    {
        return capturedPosition + (currentParentSize - capturedParentSize) * anchor;
    }


    public static bool TryGetParentPositionAdjustment(
        Matrix3x2 parentToViewport,
        Matrix3x2 wheelToParent,
        Vector2 wheelSize,
        Vector2 desiredCenterInViewport,
        out Vector2 adjustment)
    {
        if (!Matrix3x2.Invert(parentToViewport, out var viewportToParent))
        {
            adjustment = default;
            return false;
        }

        var desiredCenterInParent = Vector2.Transform(desiredCenterInViewport, viewportToParent);
        var currentCenterInParent = GetTransformedCenter(wheelToParent, wheelSize);
        adjustment = desiredCenterInParent - currentCenterInParent;
        return float.IsFinite(adjustment.X) && float.IsFinite(adjustment.Y);
    }
}
