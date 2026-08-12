using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.Patches;

/// <summary>
/// Expands room UI that vanilla prebuilds for four players. The gameplay and
/// multiplayer synchronizers remain authoritative; this patch only supplies
/// enough client-side nodes to display and complete their existing results.
/// </summary>
public static class ExtendedMultiplayerRoomPatches
{
    private const int VanillaPlayerCapacity = 4;
    private const string TreasureHolderScene = "ui/treasure_relic_holder";

    private static readonly FieldInfo TreasureMultiplayerHoldersField =
        AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_multiplayerHolders");
    private static readonly FieldInfo TreasureHoldersInUseField =
        AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse");
    private static readonly FieldInfo TreasureRelicContainerField =
        AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_relicContainer");
    private static readonly FieldInfo TreasureRunStateField =
        AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_runState");
    private static readonly FieldInfo RestSiteCharacterContainersField =
        AccessTools.Field(typeof(NRestSiteRoom), "_characterContainers");
    private static readonly FieldInfo RestSiteRunStateField =
        AccessTools.Field(typeof(NRestSiteRoom), "_runState");

    private static readonly HashSet<int> LoggedTreasureHolderCounts = new HashSet<int>();
    private static readonly HashSet<int> LoggedRestSitePlayerCounts = new HashSet<int>();
    private static bool _loggedFocusFallbackFailure;

    public static void Apply(Harmony harmony)
    {
        if (TreasureMultiplayerHoldersField != null
            && TreasureHoldersInUseField != null
            && TreasureRelicContainerField != null
            && TreasureRunStateField != null)
        {
            PatchHelper.Patch(
                harmony,
                typeof(NTreasureRoomRelicCollection),
                nameof(NTreasureRoomRelicCollection.InitializeRelics),
                prefix: PatchHelper.Method(typeof(ExtendedMultiplayerRoomPatches), nameof(TreasureInitializePrefix)),
                postfix: PatchHelper.Method(typeof(ExtendedMultiplayerRoomPatches), nameof(TreasureInitializePostfix)));
            PatchHelper.PatchGetter(
                harmony,
                typeof(NTreasureRoomRelicCollection),
                nameof(NTreasureRoomRelicCollection.DefaultFocusedControl),
                prefix: PatchHelper.Method(typeof(ExtendedMultiplayerRoomPatches), nameof(TreasureDefaultFocusPrefix)));
        }
        else
        {
            PatchHelper.Log("Extended multiplayer treasure patch skipped: required fields were not found");
        }

        PatchHelper.Patch(
            harmony,
            typeof(NHandImage),
            nameof(NHandImage._Ready),
            postfix: PatchHelper.Method(typeof(ExtendedMultiplayerRoomPatches), nameof(HandReadyPostfix)));

        if (RestSiteCharacterContainersField != null && RestSiteRunStateField != null)
        {
            PatchHelper.Patch(
                harmony,
                typeof(NRestSiteRoom),
                nameof(NRestSiteRoom._Ready),
                prefix: PatchHelper.Method(typeof(ExtendedMultiplayerRoomPatches), nameof(RestSiteReadyPrefix)),
                postfix: PatchHelper.Method(typeof(ExtendedMultiplayerRoomPatches), nameof(RestSiteReadyPostfix)));
        }
        else
        {
            PatchHelper.Log("Extended multiplayer rest-site patch skipped: required fields were not found");
        }
    }

    public static void TreasureInitializePrefix(NTreasureRoomRelicCollection __instance)
    {
        try
        {
            var runState = TreasureRunStateField.GetValue(__instance) as IRunState;
            if ((runState?.Players?.Count ?? 0) <= VanillaPlayerCapacity)
                return;

            var currentRelics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
            var requiredCount = currentRelics?.Count ?? 0;
            var holders = GetTreasureMultiplayerHolders(__instance);
            var container = TreasureRelicContainerField.GetValue(__instance) as Control;
            if (requiredCount <= holders.Count || container == null)
                return;

            var originalCount = holders.Count;
            while (holders.Count < requiredCount)
            {
                var holder = SceneHelper.Instantiate<NTreasureRoomRelicHolder>(TreasureHolderScene);
                holder.Name = "AndroidMultiplayerRelicHolder" + (holders.Count + 1);
                holder.Visible = false;
                container.AddChild(holder);
                holders.Add(holder);
            }

            if (LoggedTreasureHolderCounts.Add(holders.Count))
                PatchHelper.Log($"Extended treasure relic holders from {originalCount} to {holders.Count}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Extended multiplayer treasure holder creation failed: {exception}");
        }
    }

    public static void TreasureInitializePostfix(NTreasureRoomRelicCollection __instance)
    {
        try
        {
            var runState = TreasureRunStateField.GetValue(__instance) as IRunState;
            if ((runState?.Players?.Count ?? 0) <= VanillaPlayerCapacity)
                return;

            var currentRelics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
            if ((currentRelics?.Count ?? 0) <= VanillaPlayerCapacity)
                return;

            var container = TreasureRelicContainerField.GetValue(__instance) as Control;
            var holders = GetTreasureHoldersInUse(__instance)
                .Where(holder => holder != null && GodotObject.IsInstanceValid(holder) && holder.Visible)
                .ToList();
            if (container == null || holders.Count == 0)
                return;

            LayoutTreasureHolders(container, holders);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Extended multiplayer treasure layout failed: {exception}");
        }
    }

    public static bool TreasureDefaultFocusPrefix(
        NTreasureRoomRelicCollection __instance,
        ref Control __result)
    {
        try
        {
            var allHolders = GetTreasureHoldersInUse(__instance);
            var holders = allHolders
                .Where(holder => holder != null && GodotObject.IsInstanceValid(holder) && holder.Visible)
                .ToList();
            if (holders.Count == 0)
            {
                __result = null;
                return false;
            }

            var slotIndex = 0;
            var runState = TreasureRunStateField.GetValue(__instance) as IRunState;
            if (runState != null)
            {
                var localPlayer = LocalContext.GetMe(runState.Players);
                if (localPlayer != null)
                    slotIndex = runState.GetPlayerSlotIndex(localPlayer);
            }

            if (slotIndex < 0)
                slotIndex = 0;
            __result = holders[slotIndex % holders.Count];
            return false;
        }
        catch (Exception exception)
        {
            if (!_loggedFocusFallbackFailure)
            {
                _loggedFocusFallbackFailure = true;
                PatchHelper.Log($"Extended multiplayer treasure focus fallback failed: {exception}");
            }
            __result = null;
            return false;
        }
    }

    public static void HandReadyPostfix(NHandImage __instance)
    {
        try
        {
            var playerCount = __instance.Player?.RunState?.Players?.Count ?? 0;
            if (playerCount <= VanillaPlayerCapacity)
                return;

            var rotation = Mathf.Tau * __instance.Index / playerCount;
            if (rotation > Mathf.Pi)
                rotation -= Mathf.Tau;
            __instance.Rotation = rotation;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Extended multiplayer treasure hand layout failed: {exception.Message}");
        }
    }

    public static void RestSiteReadyPrefix(NRestSiteRoom __instance)
    {
        try
        {
            var runState = RestSiteRunStateField.GetValue(__instance) as IRunState;
            var playerCount = runState?.Players?.Count ?? 0;
            if (playerCount <= VanillaPlayerCapacity)
                return;

            var containers = GetRestSiteCharacterContainers(__instance);
            if (containers.Count == 0)
            {
                var background = __instance.GetNode<Control>("BgContainer");
                for (var index = 0; index < playerCount; index++)
                {
                    Control container;
                    if (index < VanillaPlayerCapacity)
                    {
                        container = background.GetNode<Control>("Character_" + (index + 1));
                    }
                    else
                    {
                        container = new Control
                        {
                            Name = "AndroidCharacter_" + (index + 1),
                            MouseFilter = Control.MouseFilterEnum.Ignore,
                            Scale = Vector2.One * 0.5f
                        };
                        background.AddChild(container);
                    }
                    containers.Add(container);
                }
            }

            LayoutRestSiteCharacters(containers.Take(playerCount).ToList());
            if (LoggedRestSitePlayerCounts.Add(playerCount))
                PatchHelper.Log($"Extended rest-site character containers to {playerCount}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Extended multiplayer rest-site setup failed: {exception}");
        }
    }

    public static void RestSiteReadyPostfix(NRestSiteRoom __instance)
    {
        try
        {
            var runState = RestSiteRunStateField.GetValue(__instance) as IRunState;
            var playerCount = runState?.Players?.Count ?? 0;
            if (playerCount <= VanillaPlayerCapacity)
                return;

            // The prefix inserts the correctly ordered list before vanilla appends
            // its four fixed nodes. Remove those trailing duplicate references.
            var containers = GetRestSiteCharacterContainers(__instance);
            if (containers.Count > playerCount)
                containers.RemoveRange(playerCount, containers.Count - playerCount);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Extended multiplayer rest-site cleanup failed: {exception}");
        }
    }

    private static List<NTreasureRoomRelicHolder> GetTreasureMultiplayerHolders(
        NTreasureRoomRelicCollection collection)
    {
        return (List<NTreasureRoomRelicHolder>)TreasureMultiplayerHoldersField.GetValue(collection);
    }

    private static List<NTreasureRoomRelicHolder> GetTreasureHoldersInUse(
        NTreasureRoomRelicCollection collection)
    {
        return (List<NTreasureRoomRelicHolder>)TreasureHoldersInUseField.GetValue(collection);
    }

    private static List<Control> GetRestSiteCharacterContainers(NRestSiteRoom room)
    {
        return (List<Control>)RestSiteCharacterContainersField.GetValue(room);
    }

    private static void LayoutTreasureHolders(
        Control container,
        IReadOnlyList<NTreasureRoomRelicHolder> holders)
    {
        var containerSize = container.Size;
        if (containerSize.X < 1f || containerSize.Y < 1f)
            containerSize = new Vector2(900f, 580f);

        var usableSize = new Vector2(
            Mathf.Max(320f, containerSize.X - 48f),
            Mathf.Max(260f, containerSize.Y - 64f));
        var usableOrigin = (containerSize - usableSize) * 0.5f;
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(holders.Count)));
        var rows = (holders.Count + columns - 1) / columns;
        var cellSize = new Vector2(usableSize.X / columns, usableSize.Y / rows);

        for (var index = 0; index < holders.Count; index++)
        {
            var holder = holders[index];
            var holderSize = holder.Size;
            if (holderSize.X < 1f || holderSize.Y < 1f)
                holderSize = new Vector2(136f, 136f);

            var row = index / columns;
            var column = index % columns;
            var itemsInRow = Math.Min(columns, holders.Count - row * columns);
            var rowWidth = itemsInRow * cellSize.X;
            var rowOriginX = usableOrigin.X + (usableSize.X - rowWidth) * 0.5f;
            var center = new Vector2(
                rowOriginX + (column + 0.5f) * cellSize.X,
                usableOrigin.Y + (row + 0.5f) * cellSize.Y);
            var scale = Mathf.Min(
                1f,
                Mathf.Min(
                    Mathf.Max(0.2f, (cellSize.X - 16f) / holderSize.X),
                    Mathf.Max(0.2f, (cellSize.Y - 16f) / holderSize.Y)));

            ResetAnchors(holder);
            holder.PivotOffset = holderSize * 0.5f;
            holder.Scale = Vector2.One * scale;
            holder.Position = center - holderSize * 0.5f;
        }
    }

    private static void LayoutRestSiteCharacters(IReadOnlyList<Control> containers)
    {
        if (containers.Count == 0)
            return;

        if (containers.Count <= 8)
        {
            const float centerX = 932f;
            const float topY = 583f;
            const float bottomY = 649f;
            var span = Mathf.Min(1000f, 615f + Math.Max(0, containers.Count - 4) * 100f);
            var scale = 0.5f * Mathf.Min(1f, 6f / containers.Count);
            for (var index = 0; index < containers.Count; index++)
            {
                var progress = containers.Count == 1 ? 0.5f : (float)index / (containers.Count - 1);
                ResetAnchors(containers[index]);
                containers[index].Position = new Vector2(
                    centerX + (progress - 0.5f) * span,
                    topY + (bottomY - topY) * Mathf.Abs(progress * 2f - 1f));
                containers[index].Scale = Vector2.One * scale;
            }
            return;
        }

        var columns = Math.Min(10, (int)Math.Ceiling(Math.Sqrt(containers.Count * 2f)));
        var rows = (containers.Count + columns - 1) / columns;
        var cellWidth = 1200f / columns;
        var cellHeight = 260f / rows;
        var gridScale = Mathf.Max(0.2f, Mathf.Min(0.4f, 0.5f * 3f / rows));
        for (var index = 0; index < containers.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var itemsInRow = Math.Min(columns, containers.Count - row * columns);
            var rowWidth = itemsInRow * cellWidth;
            ResetAnchors(containers[index]);
            containers[index].Position = new Vector2(
                932f - rowWidth * 0.5f + (column + 0.5f) * cellWidth,
                510f + (row + 0.5f) * cellHeight);
            containers[index].Scale = Vector2.One * gridScale;
        }
    }

    private static void ResetAnchors(Control control)
    {
        control.AnchorLeft = 0f;
        control.AnchorTop = 0f;
        control.AnchorRight = 0f;
        control.AnchorBottom = 0f;
    }
}
