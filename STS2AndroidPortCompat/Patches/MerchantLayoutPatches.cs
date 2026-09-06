using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2Mobile.Patches;

// Keep the shop's open position relative to its layout anchor. ContentScaleSize
// excludes global_scale, and an absolute tween target overrides Godot's anchor
// adjustment when the available control area changes, even during the animation.
public static class MerchantLayoutPatches
{
    public static void Apply(Harmony harmony)
    {
        var sts2Asm = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;

        var merchantInvType = sts2Asm.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory"
        );
        if (merchantInvType != null)
        {
            PatchHelper.Patch(
                harmony,
                merchantInvType,
                "DoOpenAnimation",
                prefix: PatchHelper.Method(
                    typeof(MerchantLayoutPatches),
                    nameof(MerchantOpenPrefix)
                )
            );
        }
    }

    public static bool MerchantOpenPrefix(object __instance, ref Task __result)
    {
        try
        {
            var node = (Control)__instance;

            var instType = __instance.GetType();
            var slotsContainer = (Control)
                AccessTools.Field(instType, "_slotsContainer").GetValue(__instance);
            var backstop = (Node)AccessTools.Field(instType, "_backstop").GetValue(__instance);

            float anchorTop = slotsContainer.AnchorTop;
            float openOffset = 80f - 1080f * anchorTop;

            var existingTween =
                AccessTools.Field(instType, "_inventoryTween")?.GetValue(__instance) as Tween;
            existingTween?.Kill();

            var tween = ((Node)__instance).CreateTween().SetParallel();
            tween
                .TweenProperty(backstop, "modulate:a", 0.8f, 1.0)
                .SetEase(Tween.EaseType.InOut)
                .SetTrans(Tween.TransitionType.Sine)
                .FromCurrent();
            tween
                .TweenMethod(Callable.From<float>(offset =>
                {
                    var position = slotsContainer.Position;
                    position.Y = node.Size.Y * anchorTop + offset;
                    slotsContainer.Position = position;
                }), slotsContainer.OffsetTop, openOffset, 0.7)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Quint);

            AccessTools.Field(instType, "_inventoryTween")?.SetValue(__instance, tween);

            PatchHelper.Log($"Merchant open: y={node.Size.Y * anchorTop + openOffset} (control height: {node.Size.Y})");

            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"MerchantOpenPrefix failed: {ex.Message}");
            return true;
        }
    }
}
