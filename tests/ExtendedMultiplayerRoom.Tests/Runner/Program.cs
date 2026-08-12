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
        TestFivePlayerTreasureRoom();
        TestTreasureFocusWhenRelicsAreSuppressed();
        TestExtendedTreasureHands();
        TestRestSiteContainers(5);
        TestRestSiteContainers(17);
        Console.WriteLine("Extended multiplayer room regression test passed.");
        return 0;
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

        var collection = new NTreasureRoomRelicCollection(runState);
        ExtendedMultiplayerRoomPatches.TreasureInitializePrefix(collection);
        Assert(collection.MultiplayerHolders.Count == 5,
            "five backend relics must create a fifth client holder");

        collection.SimulateVanillaInitializeRelics(5);
        ExtendedMultiplayerRoomPatches.TreasureInitializePostfix(collection);

        Assert(collection.HoldersInUse.Count == 5, "all five holders must enter vanilla award processing");
        Assert(collection.HoldersInUse.All(IsFinite), "all five holder layouts must remain finite");
        Assert(collection.HoldersInUse.Select(holder => holder.Position).Distinct().Count() == 5,
            "five holders must occupy distinct positions");
        Assert(collection.HoldersInUse.All(holder => holder.Scale.X > 0f && holder.Scale.X <= 1f),
            "extended holder scale must remain visible and bounded");

        Control focused = null;
        var runOriginal = ExtendedMultiplayerRoomPatches.TreasureDefaultFocusPrefix(collection, ref focused);
        Assert(!runOriginal, "compat focus prefix must replace the unsafe vanilla getter");
        Assert(ReferenceEquals(focused, collection.HoldersInUse[4]),
            "the fifth player must focus the fifth relic when it exists");
    }

    private static void TestTreasureFocusWhenRelicsAreSuppressed()
    {
        var runState = CreateRunState(5);
        LocalContext.LocalPlayer = runState.Players[4];
        RunManager.Instance = new RunManager
        {
            TreasureRoomRelicSynchronizer = new TreasureRoomRelicSynchronizer(4)
        };

        var collection = new NTreasureRoomRelicCollection(runState);
        collection.SimulateVanillaInitializeRelics(4);
        Control focused = null;
        ExtendedMultiplayerRoomPatches.TreasureDefaultFocusPrefix(collection, ref focused);
        Assert(ReferenceEquals(focused, collection.HoldersInUse[0]),
            "a player without a matching relic slot must safely wrap to a visible holder");
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
