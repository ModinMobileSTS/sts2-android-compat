using System;

namespace STS2Mobile.Patches;

internal static class MobileReactionVisibilityPolicy
{
    internal static bool IsMobileRuntime(bool hasMobileFeature, string osName)
    {
        return hasMobileFeature
            || string.Equals(osName, "Android", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldDisplay(
        bool hasMobileFeature,
        string osName,
        bool settingEnabled,
        bool reactionAvailable)
    {
        return IsMobileRuntime(hasMobileFeature, osName)
            && settingEnabled
            && reactionAvailable;
    }
}
