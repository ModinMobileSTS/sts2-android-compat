using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Patches;

internal static class Program
{
    private static int Main()
    {
        TestVanillaCapacityRemainsUnchanged();
        RunTreasureScenarios();
        TestExtendedTreasureHands(5);
        TestExtendedTreasureHands(17);
        TestRestSiteContainers(5);
        TestRestSiteContainers(17);
        Console.WriteLine("Extended multiplayer room regression test passed.");
        return 0;
    }

    internal static void RunTreasureScenarios()
    {
        TestExtendedTreasureRoom(5);
        TestExtendedTreasureRoom(17);
#if TREASURE_HOLDER_LAYOUT_SHAPE
        for (var relicCount = 1; relicCount <= 6; relicCount++)
            TestAuthoredTreasureLayoutRemainsUnchanged(relicCount);
        TestTreasureLayoutReuse();
#endif
        TestTreasureFocusWhenRelicsAreSuppressed(5, 4);
        TestTreasureFocusWhenRelicsAreSuppressed(17, 5);
        TestFocusWithoutContainer();
        TestAwardCompletion(4, 4, false);
        TestAwardCompletion(5, 5, false);
        TestAwardCompletion(5, 5, true);
        TestAwardCompletion(5, 4, true);
        TestAwardCompletion(5, 0, false);
        TestAwardCompletion(17, 17, false);
        TestAwardCompletion(17, 17, true);
#if NATIVE_GODOT
#if TREASURE_HOLDER_LAYOUT_SHAPE
        GD.Print("PASS: native treasure authored-layout shape; 1–6 layouts/reveal preserved, 5/17-player focus/awards and overflow grid reuse.");
#elif LEGACY_TREASURE_SHAPE
        GD.Print("PASS: native treasure legacy Container shape; five-player focus/awards, contest/skip/suppression, unchanged four-player flow.");
#else
        GD.Print("PASS: native treasure field-backed Container shape; five-player focus/awards, contest/skip/suppression, unchanged four-player flow.");
#endif
#endif
    }

    private static IReadOnlyList<NTreasureRoomRelicHolder> GetAvailableHolders(
        NTreasureRoomRelicCollection collection,
        int relicCount)
    {
#if TREASURE_HOLDER_LAYOUT_SHAPE
        return relicCount > 0 && relicCount <= collection.HolderLayouts.Count
            ? collection.HolderLayouts[relicCount - 1].GetChildren().OfType<NTreasureRoomRelicHolder>().ToArray()
            : Array.Empty<NTreasureRoomRelicHolder>();
#else
        return collection.MultiplayerHolders;
#endif
    }

    private static NTreasureRoomRelicCollection CreateCollection(IRunState state)
    {
        var collection = new NTreasureRoomRelicCollection(state);
#if NATIVE_GODOT
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(collection);
#endif
        return collection;
    }

    private static void Release(Node node)
    {
#if NATIVE_GODOT
        node.Free();
#endif
    }

    private static void InitializeCollection(NTreasureRoomRelicCollection collection)
    {
#if !NATIVE_GODOT
        ExtendedMultiplayerRoomPatches.TreasureInitializePrefix(collection);
#endif
        collection.InitializeRelics();
#if !NATIVE_GODOT
        ExtendedMultiplayerRoomPatches.TreasureInitializePostfix(collection);
#endif
    }

    private static Control GetFocus(NTreasureRoomRelicCollection collection)
    {
#if NATIVE_GODOT
        return collection.DefaultFocusedControl;
#else
        Control result = null;
        return ExtendedMultiplayerRoomPatches.TreasureDefaultFocusPrefix(collection, ref result)
            ? collection.DefaultFocusedControl : result;
#endif
    }

    private static void TestVanillaCapacityRemainsUnchanged()
    {
        var runState = CreateRunState(4);
        RunManager.Instance = new RunManager
        {
            TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(5)
        };

        var collection = new NTreasureRoomRelicCollection(runState);
        ExtendedMultiplayerRoomPatches.TreasureInitializePrefix(collection);
        Assert(GetAvailableHolders(collection, 4).Count == 4,
            "four-player treasure rooms must retain the vanilla holder set");

        var hand = new NHandImage(runState.Players[3], 3) { Rotation = 1.25f };
        ExtendedMultiplayerRoomPatches.HandReadyPostfix(hand);
        Assert(Math.Abs(hand.Rotation - 1.25f) < 0.0001f,
            "four-player hand placement must remain untouched");

        var room = new NRestSiteRoom(runState);
        ExtendedMultiplayerRoomPatches.RestSiteReadyPrefix(room);
        Assert(room.CharacterContainers.Count == 0,
            "four-player rest sites must remain owned by vanilla setup");
    }

    private static void TestExtendedTreasureRoom(int playerCount)
    {
        var runState = CreateRunState(playerCount);
        LocalContext.LocalPlayer = runState.Players[playerCount - 1];
        RunManager.Instance = new RunManager
        {
            TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(playerCount)
        };

        var collection = CreateCollection(runState);
        try
        {
            InitializeCollection(collection);
            Assert(GetAvailableHolders(collection, playerCount).Count == playerCount,
                $"{playerCount} backend relics must have exactly one client holder each");
            Assert(collection.HoldersInUse.Count == playerCount, "all holders must enter vanilla award processing");
            Assert(collection.HoldersInUse.All(IsFinite), "extended holder layouts must remain finite");
            Assert(collection.HoldersInUse.Select(holder => holder.Position).Distinct().Count() == playerCount,
                "extended holders must occupy distinct positions");
            Assert(collection.HoldersInUse.All(holder => holder.Scale.X > 0f && holder.Scale.X <= 1f),
                "extended holder scale must remain visible and bounded");
            Assert(collection.HoldersInUse.Select(holder => holder.Index).SequenceEqual(Enumerable.Range(0, playerCount)),
                "vanilla initialization must retain relic-index ordering for every holder");
#if TREASURE_HOLDER_LAYOUT_SHAPE
            AssertHighResolutionReveal(collection.HoldersInUse);
#endif

            Control focused = GetFocus(collection);
            Assert(ReferenceEquals(focused, collection.HoldersInUse[playerCount - 1]),
                "the last player must focus their matching relic when it exists");
#if NATIVE_GODOT
            focused.GrabFocus();
            Assert(focused.HasFocus(), "the returned extended holder must accept native Godot focus");
#endif
            var originalHolders = GetAvailableHolders(collection, playerCount).ToArray();
            InitializeCollection(collection);
            Assert(GetAvailableHolders(collection, playerCount).SequenceEqual(originalHolders),
                "reinitialization must reuse holders rather than append duplicates");
        }
        finally { Release(collection); }
    }

#if TREASURE_HOLDER_LAYOUT_SHAPE
    private static void AssertHighResolutionReveal(IEnumerable<NTreasureRoomRelicHolder> holders)
    {
        foreach (var holder in holders)
        {
            Assert(holder.Relic != null && holder.Relic.UsesLargeIcon,
                "vanilla Initialize must create the new high-resolution relic child");
            Assert(ReferenceEquals(holder.Relic.GetParent(), holder)
                && holder.Relic.Position.Equals(new Vector2(68f, 68f))
                && holder.Relic.Scale.Equals(Vector2.One * 2f),
                "layout adaptation must leave the relic child's reveal position, scale and parent untouched");
        }
    }

    private static void TestAuthoredTreasureLayoutRemainsUnchanged(int relicCount)
    {
        var runState = CreateRunState(5);
        LocalContext.LocalPlayer = runState.Players[4];
        RunManager.Instance = new RunManager { TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(relicCount) };
        var collection = CreateCollection(runState);
        try
        {
            var layouts = collection.HolderLayouts.ToArray();
            var holders = GetAvailableHolders(collection, relicCount).ToArray();
            var geometry = holders.Select(HolderGeometry).ToArray();
#if !NATIVE_GODOT
            ExtendedMultiplayerRoomPatches.TreasureInitializePrefix(collection);
            Assert(holders.All(holder => holder.Relic == null && holder.InitializationCount == 0),
                "the prefix must not pre-create relic icons or replace vanilla reveal initialization");
#endif
            InitializeCollection(collection);
            Assert(collection.HolderLayouts.SequenceEqual(layouts)
                && collection.HoldersInUse.SequenceEqual(holders),
                $"the authored {relicCount}-relic layout and its holder nodes must be reused");
            Assert(geometry.SequenceEqual(holders.Select(HolderGeometry)),
                $"the authored {relicCount}-relic positions, anchors, pivots and scales must remain unchanged");
            Assert(layouts.Count(layout => layout.Visible) == 1 && layouts[relicCount - 1].Visible,
                "only vanilla's selected authored layout may be visible");
            Assert(ReferenceEquals(GetFocus(collection), holders[4 % relicCount]),
                "focus must wrap safely even when the authored relic layout is smaller than the player count");
            AssertHighResolutionReveal(holders);
            Assert(holders.All(holder => holder.InitializationCount == 1),
                "only vanilla initialization may initialize the authored holders");
        }
        finally { Release(collection); }
    }

    private static void TestTreasureLayoutReuse()
    {
        var runState = CreateRunState(17);
        LocalContext.LocalPlayer = runState.Players[16];
        var collection = CreateCollection(runState);
        try
        {
            var authoredLayouts = collection.HolderLayouts.ToArray();
            NTreasureRoomRelicHolder[] seventeenHolders = null;
            Control[] expandedLayouts = null;
            foreach (var count in new[] { 17, 7, 5, 17, 17 })
            {
                RunManager.Instance = new RunManager { TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(count) };
                InitializeCollection(collection);
                Assert(collection.HolderLayouts.Count == 17
                    && collection.HolderLayouts.Take(6).SequenceEqual(authoredLayouts),
                    "regeneration must preserve authored layouts and reuse indexed overflow slots");
                Assert(collection.GetNode<Control>("Container").GetChildCount() == 17
                    && collection.HolderLayouts.Distinct().Count() == 17,
                    "regeneration must not leak extra or duplicate layout nodes into the scene");
                Assert(collection.HoldersInUse.Count == count
                    && collection.HoldersInUse.Distinct().Count() == count,
                    "a previously reserved layout must receive exactly its requested holder count");
                Assert(collection.HoldersInUse.Select(holder => holder.Relic.Model)
                    .SequenceEqual(RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics),
                    "layout switching must preserve the new authoritative relic order");
                Assert(collection.HolderLayouts.Count(layout => layout.Visible) == 1
                    && collection.HolderLayouts[count - 1].Visible,
                    "regeneration must hide all old layouts rather than leave duplicate visible grids");
                AssertHighResolutionReveal(collection.HoldersInUse);
                if (expandedLayouts == null)
                {
                    expandedLayouts = collection.HolderLayouts.ToArray();
                    seventeenHolders = collection.HoldersInUse.ToArray();
                    Assert(collection.HolderLayouts.Skip(6).Take(10).All(layout => layout.GetChildCount() == 0),
                        "reserving intermediate layout indices must not allocate unused holder grids");
                }
                else
                {
                    Assert(collection.HolderLayouts.SequenceEqual(expandedLayouts),
                        "regeneration must not append replacement or duplicate layout nodes");
                    if (count == 17)
                        Assert(collection.HoldersInUse.SequenceEqual(seventeenHolders),
                            "returning to the larger grid must reuse its original holders");
                }
            }
        }
        finally { Release(collection); }
    }
#endif

    private static (Vector2 Position, Vector2 Size, Vector2 Scale, Vector2 Pivot, float Rotation,
        float Left, float Top, float Right, float Bottom) HolderGeometry(NTreasureRoomRelicHolder holder) =>
        (holder.Position, holder.Size, holder.Scale, holder.PivotOffset, holder.Rotation,
            holder.AnchorLeft, holder.AnchorTop, holder.AnchorRight, holder.AnchorBottom);

    private static void TestTreasureFocusWhenRelicsAreSuppressed(int playerCount, int relicCount)
    {
        var runState = CreateRunState(playerCount);
        LocalContext.LocalPlayer = runState.Players[playerCount - 1];
        RunManager.Instance = new RunManager
        {
            TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(relicCount)
        };

        var collection = CreateCollection(runState);
        try
        {
            InitializeCollection(collection);
            Assert(ReferenceEquals(GetFocus(collection), collection.HoldersInUse[(playerCount - 1) % relicCount]),
                "a player without a matching relic slot must safely wrap to a visible holder");
        }
        finally { Release(collection); }
    }

    private static void TestFocusWithoutContainer()
    {
        var runState = CreateRunState(5);
        LocalContext.LocalPlayer = runState.Players[4];
        RunManager.Instance = new RunManager { TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(4) };
        var collection = CreateCollection(runState);
        var container = collection.GetNode<Control>("Container");
        try
        {
            InitializeCollection(collection);
            collection.RemoveChild(container);
            Assert(ReferenceEquals(GetFocus(collection), collection.HoldersInUse[0]),
                "focus safety must not depend on resolving the layout container");
        }
        finally
        {
            collection.AddChild(container);
            Release(collection);
        }
    }

    private static void TestAwardCompletion(int playerCount, int relicCount, bool contestAndSkip)
    {
        var runState = CreateRunState(playerCount);
        LocalContext.LocalPlayer = runState.Players[playerCount - 1];
        var synchronizer = new TreasureRoomRelicSynchronizer(relicCount);
        RunManager.Instance = new RunManager { TreasureRoomRelicSynchronizer = synchronizer };
        var collection = CreateCollection(runState);
        var vanillaHolders = GetAvailableHolders(collection, relicCount).ToArray();
        var vanillaLayout = vanillaHolders.Select(HolderGeometry).ToArray();
        try
        {
            InitializeCollection(collection);
            if (playerCount == 4)
                Assert(GetAvailableHolders(collection, relicCount).SequenceEqual(vanillaHolders)
                    && vanillaLayout.SequenceEqual(vanillaHolders.Select(HolderGeometry)),
                    "four-player initialization must retain the original holders and geometry");
            if (relicCount == 0)
                Assert(GetFocus(collection) == null, "no visible relics must yield no default focus");

            // Already-decided backend results; the client must neither reroll nor reassign them.
            var results = synchronizer.CurrentRelics.Select((relic, index) => (
                Relic: relic,
                Recipient: contestAndSkip && index == 1 ? null
                    : contestAndSkip && (index == 0 || index == 2) ? runState.Players[playerCount - 1]
                    : runState.Players[index % playerCount])).ToArray();
            collection.CompleteAwards(results);
            Assert(collection.AwardsFinished && collection.ProcessedRelics.SequenceEqual(synchronizer.CurrentRelics),
                "every generated relic, including a skipped relic, must find a holder and finish award processing");
            foreach (var player in runState.Players)
                Assert(player.AwardedRelics.SequenceEqual(results.Where(result => ReferenceEquals(result.Recipient, player)).Select(result => result.Relic)),
                    "UI expansion must preserve the backend's exact award ownership");
        }
        finally { Release(collection); }
    }

    private static void TestExtendedTreasureHands(int playerCount)
    {
        var runState = CreateRunState(playerCount);
        var rotations = new List<float>();
        for (var index = 0; index < runState.Players.Count; index++)
        {
            var hand = new NHandImage(runState.Players[index], index);
            ExtendedMultiplayerRoomPatches.HandReadyPostfix(hand);
            rotations.Add(hand.Rotation);
        }

        Assert(rotations.Distinct().Count() == playerCount,
            "extended award/RPS hands must not share the vanilla four-player direction");
        Assert(rotations.All(rotation => float.IsFinite(rotation) && rotation >= -Mathf.Pi && rotation <= Mathf.Pi),
            "extended hand angles must remain finite and normalized");
        Assert(Math.Abs(rotations[playerCount - 1] + Mathf.Tau / playerCount) < 0.0001f,
            "the last hand must use its evenly distributed edge angle");
    }

    private static void TestRestSiteContainers(int playerCount)
    {
        var runState = CreateRunState(playerCount);
        var room = new NRestSiteRoom(runState);
        ExtendedMultiplayerRoomPatches.RestSiteReadyPrefix(room);

        Assert(room.CharacterContainers.Count == playerCount,
            $"rest site must contain one ordered slot for each of {playerCount} players before vanilla indexing");
        Assert(room.CharacterContainers.Distinct().Count() == playerCount,
            "rest-site slots must be unique before vanilla character creation");
        Assert(room.CharacterContainers.All(IsFinite), "rest-site slot layouts must remain finite");

        var originalContainers = room.CharacterContainers.ToArray();
        for (var readyCount = 0; readyCount < 2; readyCount++)
        {
            var nodeCount = room.GetNode<Control>("BgContainer").GetChildCount();
            ExtendedMultiplayerRoomPatches.RestSiteReadyPrefix(room);
            room.SimulateVanillaReady();
            ExtendedMultiplayerRoomPatches.RestSiteReadyPostfix(room);
            Assert(room.CharacterContainers.SequenceEqual(originalContainers),
                "repeated Ready must remove appended vanilla references without changing slot ordering");
            Assert(room.GetNode<Control>("BgContainer").GetChildCount() == nodeCount,
                "repeated Ready must not leak replacement character-container nodes");
            Assert(room.CreatedCharacters.Skip(readyCount * playerCount).Take(playerCount)
                .Select(character => character.GetParent()).SequenceEqual(originalContainers),
                "vanilla character creation must index each player's unique ordered container");
        }
    }

    private static SyntheticRunState CreateRunState(int count)
    {
        var runState = new SyntheticRunState();
        for (var index = 0; index < count; index++)
            runState.Add(new Player());
        return runState;
    }

    private static bool IsFinite(Control control)
    {
        return float.IsFinite(control.Position.X)
            && float.IsFinite(control.Position.Y)
            && float.IsFinite(control.Scale.X)
            && float.IsFinite(control.Scale.Y);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
