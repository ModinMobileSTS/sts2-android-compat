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
        TestExtendedTreasureHands();
        TestRestSiteContainers(5);
        TestRestSiteContainers(17);
        Console.WriteLine("Extended multiplayer room regression test passed.");
        return 0;
    }

    internal static void RunTreasureScenarios()
    {
        TestFivePlayerTreasureRoom();
        TestTreasureFocusWhenRelicsAreSuppressed();
        TestFocusWithoutContainer();
        TestAwardCompletion(4, 4, false);
        TestAwardCompletion(5, 5, false);
        TestAwardCompletion(5, 5, true);
        TestAwardCompletion(5, 4, true);
        TestAwardCompletion(5, 0, false);
#if NATIVE_GODOT
#if LEGACY_TREASURE_SHAPE
        GD.Print("PASS: native treasure legacy Container shape; five-player focus/awards, contest/skip/suppression, unchanged four-player flow.");
#else
        GD.Print("PASS: native treasure field-backed Container shape; five-player focus/awards, contest/skip/suppression, unchanged four-player flow.");
#endif
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
        Assert(collection.MultiplayerHolders.Count == 4,
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

    private static void TestFivePlayerTreasureRoom()
    {
        var runState = CreateRunState(5);
        LocalContext.LocalPlayer = runState.Players[4];
        RunManager.Instance = new RunManager
        {
            TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(5)
        };

        var collection = CreateCollection(runState);
        try
        {
            InitializeCollection(collection);
            Assert(collection.MultiplayerHolders.Count == 5,
                "five backend relics must create a fifth client holder");
            Assert(collection.HoldersInUse.Count == 5, "all five holders must enter vanilla award processing");
            Assert(collection.HoldersInUse.All(IsFinite), "all five holder layouts must remain finite");
            Assert(collection.HoldersInUse.Select(holder => holder.Position).Distinct().Count() == 5,
                "five holders must occupy distinct positions");
            Assert(collection.HoldersInUse.All(holder => holder.Scale.X > 0f && holder.Scale.X <= 1f),
                "extended holder scale must remain visible and bounded");

            Control focused = GetFocus(collection);
            Assert(ReferenceEquals(focused, collection.HoldersInUse[4]),
                "the fifth player must focus the fifth relic when it exists");
#if NATIVE_GODOT
            focused.GrabFocus();
            Assert(focused.HasFocus(), "the returned fifth holder must accept native Godot focus");
#endif
            var originalHolders = collection.MultiplayerHolders.ToArray();
            InitializeCollection(collection);
            Assert(collection.MultiplayerHolders.SequenceEqual(originalHolders), "reinitialization must reuse holders rather than append duplicates");
        }
        finally { Release(collection); }
    }

    private static void TestTreasureFocusWhenRelicsAreSuppressed()
    {
        var runState = CreateRunState(5);
        LocalContext.LocalPlayer = runState.Players[4];
        RunManager.Instance = new RunManager
        {
            TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(4)
        };

        var collection = CreateCollection(runState);
        try
        {
            InitializeCollection(collection);
            Assert(ReferenceEquals(GetFocus(collection), collection.HoldersInUse[0]),
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
        var vanillaHolders = collection.MultiplayerHolders.ToArray();
        var vanillaLayout = vanillaHolders.Select(holder => (holder.Position, holder.Scale, holder.AnchorLeft, holder.AnchorTop, holder.AnchorRight, holder.AnchorBottom)).ToArray();
        try
        {
            InitializeCollection(collection);
            if (playerCount == 4)
                Assert(collection.MultiplayerHolders.SequenceEqual(vanillaHolders)
                    && vanillaLayout.SequenceEqual(vanillaHolders.Select(holder => (holder.Position, holder.Scale, holder.AnchorLeft, holder.AnchorTop, holder.AnchorRight, holder.AnchorBottom))),
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

    private static void TestExtendedTreasureHands()
    {
        var runState = CreateRunState(5);
        var rotations = new List<float>();
        for (var index = 0; index < runState.Players.Count; index++)
        {
            var hand = new NHandImage(runState.Players[index], index);
            ExtendedMultiplayerRoomPatches.HandReadyPostfix(hand);
            rotations.Add(hand.Rotation);
        }

        Assert(rotations.Distinct().Count() == 5,
            "five award hands must not share the vanilla four-player direction");
        Assert(Math.Abs(rotations[4] + Mathf.Tau / 5f) < 0.0001f,
            "the fifth hand must use its evenly distributed edge angle");
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

        room.SimulateVanillaFixedContainerAppend();
        ExtendedMultiplayerRoomPatches.RestSiteReadyPostfix(room);
        Assert(room.CharacterContainers.Count == playerCount,
            "trailing vanilla duplicate references must be removed after room setup");
        Assert(room.CharacterContainers.Distinct().Count() == playerCount,
            "rest-site slot ordering must remain stable after vanilla setup");
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
