using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;

namespace STS2Mobile.Patches;

/// <summary>
/// Keeps persisted Steam run-history metadata readable on Android without
/// changing the platform stored in the history file or enabling desktop
/// Steamworks. Only the identity lookups made while rendering run history are
/// redirected; Steam transport, lobby, and invite paths retain their original
/// behavior.
/// </summary>
public static class RunHistoryPatches
{
    private static bool _loggedSteamIdentityFallback;
    private static bool _loggedAndroidIdentityFallbackFailure;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(NRunHistory),
            "DisplayRun",
            transpiler: PatchHelper.Method(typeof(RunHistoryPatches), nameof(DisplayRunTranspiler)));
        PatchHelper.Patch(
            harmony,
            typeof(NRunHistory),
            "SelectPlayer",
            transpiler: PatchHelper.Method(typeof(RunHistoryPatches), nameof(SelectPlayerTranspiler)));
        PatchHelper.Patch(
            harmony,
            typeof(NRunHistoryPlayerIcon),
            "LoadRun",
            transpiler: PatchHelper.Method(typeof(RunHistoryPatches), nameof(PlayerIconLoadRunTranspiler)));
    }

    public static IEnumerable<CodeInstruction> DisplayRunTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return ReplacePlatformIdentityCalls(instructions, "NRunHistory.DisplayRun");
    }

    public static IEnumerable<CodeInstruction> SelectPlayerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return ReplacePlatformIdentityCalls(instructions, "NRunHistory.SelectPlayer");
    }

    public static IEnumerable<CodeInstruction> PlayerIconLoadRunTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        return ReplacePlatformIdentityCalls(instructions, "NRunHistoryPlayerIcon.LoadRun");
    }

    public static ulong GetRunHistoryLocalPlayerId(PlatformType platformType)
    {
        if (platformType != PlatformType.Steam)
            return PlatformUtil.GetLocalPlayerId(platformType);

        if (SteamInitializer.Initialized)
        {
            try
            {
                return PlatformUtil.GetLocalPlayerId(platformType);
            }
            catch (Exception exception) when (IsSteamNativeUnavailable(exception))
            {
                LogSteamIdentityFallback("local player ID", exception);
            }
        }
        else
        {
            LogSteamIdentityFallback("local player ID", null);
        }

        return GetAndroidLocalPlayerId();
    }

    public static string GetRunHistoryPlayerName(PlatformType platformType, ulong playerId)
    {
        if (platformType != PlatformType.Steam)
            return PlatformUtil.GetPlayerName(platformType, playerId);

        if (SteamInitializer.Initialized)
        {
            try
            {
                return PlatformUtil.GetPlayerName(platformType, playerId);
            }
            catch (Exception exception) when (IsSteamNativeUnavailable(exception))
            {
                LogSteamIdentityFallback("player name", exception);
            }
        }
        else
        {
            LogSteamIdentityFallback("player name", null);
        }

        return GetAndroidPlayerName(playerId);
    }

    private static IEnumerable<CodeInstruction> ReplacePlatformIdentityCalls(
        IEnumerable<CodeInstruction> instructions,
        string targetName)
    {
        var codes = new List<CodeInstruction>(instructions);
        var localIdReplacement = AccessTools.Method(
            typeof(RunHistoryPatches),
            nameof(GetRunHistoryLocalPlayerId));
        var playerNameReplacement = AccessTools.Method(
            typeof(RunHistoryPatches),
            nameof(GetRunHistoryPlayerName));
        var replacementCount = 0;

        foreach (var instruction in codes)
        {
            if (!(instruction.operand is MethodInfo calledMethod) || calledMethod.DeclaringType != typeof(PlatformUtil))
                continue;

            if (IsPlatformIdentityCall(calledMethod, nameof(PlatformUtil.GetLocalPlayerId), typeof(PlatformType)))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = localIdReplacement;
                replacementCount++;
                continue;
            }

            if (IsPlatformIdentityCall(calledMethod, nameof(PlatformUtil.GetPlayerName), typeof(PlatformType), typeof(ulong)))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = playerNameReplacement;
                replacementCount++;
            }
        }

        if (replacementCount == 0)
            PatchHelper.Log($"WARNING: No run-history platform identity lookup was found in {targetName}; Steam history fallback was not injected.");
        else
            PatchHelper.Log($"Run-history Steam identity fallback injected into {targetName} ({replacementCount} lookup(s)).");

        return codes;
    }

    private static bool IsPlatformIdentityCall(MethodInfo method, string name, params Type[] parameterTypes)
    {
        if (method.Name != name)
            return false;

        var parameters = method.GetParameters();
        if (parameters.Length != parameterTypes.Length)
            return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].ParameterType != parameterTypes[index])
                return false;
        }
        return true;
    }

    private static ulong GetAndroidLocalPlayerId()
    {
        try
        {
            var playerId = PlatformUtil.GetLocalPlayerId(PlatformType.None);
            return playerId == 0 ? 1UL : playerId;
        }
        catch (Exception exception)
        {
            LogAndroidIdentityFallbackFailure("local player ID", exception);
            return 1UL;
        }
    }

    private static string GetAndroidPlayerName(ulong playerId)
    {
        try
        {
            var playerName = PlatformUtil.GetPlayerName(PlatformType.None, playerId);
            if (!string.IsNullOrWhiteSpace(playerName))
                return playerName;
        }
        catch (Exception exception)
        {
            LogAndroidIdentityFallbackFailure("player name", exception);
        }

        return playerId == 0
            ? "Player"
            : playerId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSteamNativeUnavailable(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is DllNotFoundException
                || current is EntryPointNotFoundException
                || current is BadImageFormatException
                || current is PlatformNotSupportedException
                || current is NotImplementedException)
            {
                return true;
            }
        }
        return false;
    }

    private static void LogSteamIdentityFallback(string operation, Exception exception)
    {
        if (_loggedSteamIdentityFallback)
            return;
        _loggedSteamIdentityFallback = true;

        var reason = exception == null
            ? "desktop Steamworks is not initialized"
            : $"desktop Steamworks is unavailable ({exception.GetType().Name}: {exception.Message})";
        PatchHelper.Log($"Run history uses persisted Steam metadata, but {reason}; using Android identity fallback for {operation}.");
    }

    private static void LogAndroidIdentityFallbackFailure(string operation, Exception exception)
    {
        if (_loggedAndroidIdentityFallbackFailure)
            return;
        _loggedAndroidIdentityFallbackFailure = true;
        PatchHelper.Log($"Android run-history identity fallback failed for {operation}; using a stable placeholder: {exception.GetType().Name}: {exception.Message}");
    }
}
