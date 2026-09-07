using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2Mobile.Patches;

public static class IntentAnimationPatches
{
    private const float BobSpeed = Mathf.Pi;
    private const float BobDistance = 10f;
    private const float BobOffset = 8f;
    private const int AnimationFps = 24;

    private static readonly ConditionalWeakTable<NIntent, IntentAnimState> States = new();

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NIntent), "_Ready", postfix: PatchHelper.Method(typeof(IntentAnimationPatches), nameof(ReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NIntent), "_ExitTree", prefix: PatchHelper.Method(typeof(IntentAnimationPatches), nameof(ExitTreePrefix)));
        PatchHelper.Patch(harmony, typeof(NIntent), "UpdateIntent", postfix: PatchHelper.Method(typeof(IntentAnimationPatches), nameof(UpdateIntentPostfix)));
        var framesField = AccessTools.Field(typeof(NIntent), "_animationFrames");
        var processPrefix = framesField?.FieldType == typeof(List<Texture2D>)
            ? nameof(ProcessWithFramesPrefix) : nameof(ProcessPrefix);
        PatchHelper.Patch(harmony, typeof(NIntent), "_Process", prefix: PatchHelper.Method(typeof(IntentAnimationPatches), processPrefix));
    }

    public static void ReadyPostfix(NIntent __instance, Control ____intentHolder, float ____timeOffset)
    {
        try
        {
            StartBobTween(__instance, ____intentHolder, ____timeOffset);
            var state = GetState(__instance);
            state.AnimationStartTimeSeconds = GetCurrentTimeSeconds();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Intent animation ready patch failed: {exception.Message}");
        }
    }

    public static void ExitTreePrefix(NIntent __instance)
    {
        try
        {
            GetState(__instance, create: false)?.BobTween?.Kill();
            States.Remove(__instance);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Intent animation exit patch failed: {exception.Message}");
        }
    }

    public static void UpdateIntentPostfix(NIntent __instance, string ____animationName)
    {
        try
        {
            var state = GetState(__instance);
            if (!string.Equals(state.AnimationName, ____animationName, StringComparison.Ordinal))
                ResetAnimation(state, ____animationName);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Intent animation update patch failed: {exception.Message}");
        }
    }

    public static bool ProcessPrefix(NIntent __instance, string ____animationName, Sprite2D ____intentSprite)
    {
        return ProcessAnimation(__instance, ____animationName, ____intentSprite, null);
    }

    public static bool ProcessWithFramesPrefix(NIntent __instance, string ____animationName,
        Sprite2D ____intentSprite, List<Texture2D> ____animationFrames)
    {
        return ProcessAnimation(__instance, ____animationName, ____intentSprite, ____animationFrames);
    }

    private static bool ProcessAnimation(NIntent intent, string animationName, Sprite2D sprite,
        List<Texture2D> gameFrames)
    {
        try
        {
            if (string.IsNullOrEmpty(animationName))
                return false;
            var state = GetState(intent);
            // Combat-state updates may change visuals without calling UpdateIntent.
            if (!string.Equals(state.AnimationName, animationName, StringComparison.Ordinal))
                ResetAnimation(state, animationName);

            if (gameFrames == null)
                state.Frames ??= new Texture2D[IntentAnimData.GetAnimationFrameCount(animationName)];
            var frameCount = gameFrames?.Count ?? state.Frames.Length;
            if (frameCount <= 0)
                return false;
            var frame = (int)((GetCurrentTimeSeconds() - state.AnimationStartTimeSeconds) * AnimationFps) % frameCount;
            if (state.AnimationFrame != frame && sprite != null)
            {
                Texture2D texture;
                if (gameFrames != null)
                {
                    texture = gameFrames[frame];
                }
                else
                {
                    // Old payloads have no frame list. Load on first use, preserving
                    // their loading timing, and retain only this intent's animation.
                    texture = state.Frames[frame];
                    if (texture == null || !GodotObject.IsInstanceValid(texture))
                        state.Frames[frame] = texture = PreloadManager.Cache.GetTexture2D(IntentAnimData.GetAnimationFrame(animationName, frame));
                }
                sprite.Texture = texture;
                state.AnimationFrame = frame;
            }
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Intent animation process patch failed; falling back to vanilla: {exception.Message}");
            return true;
        }
    }

    private static void ResetAnimation(IntentAnimState state, string animationName)
    {
        state.AnimationName = animationName;
        state.AnimationFrame = null;
        state.AnimationStartTimeSeconds = GetCurrentTimeSeconds();
        state.Frames = null;
    }

    private static void StartBobTween(NIntent intent, Control holder, float timeOffset)
    {
        if (holder == null)
            return;

        var state = GetState(intent);
        state.BobTween?.Kill();

        var phase = Mathf.PosMod(timeOffset, Mathf.Tau);
        holder.Position = new Vector2(holder.Position.X, GetBobPositionY(phase));

        var movingUp = phase < Mathf.Pi * 0.5f || phase >= Mathf.Pi * 1.5f;
        var targetPhase = movingUp ? Mathf.Pi * 0.5f : Mathf.Pi * 1.5f;
        if (movingUp && phase >= targetPhase)
            targetPhase += Mathf.Tau;

        var duration = (targetPhase - phase) / BobSpeed;
        if (duration <= 0.001f)
        {
            StartSteadyBobTween(intent, holder, state, movingUp);
            return;
        }

        state.BobTween = intent.CreateTween();
        state.BobTween.TweenProperty(holder, "position:y", movingUp ? GetBobTopY() : GetBobBottomY(), duration)
            .SetEase(Tween.EaseType.InOut)
            .SetTrans(Tween.TransitionType.Sine);
        state.BobTween.TweenCallback(Callable.From(() => StartSteadyBobTween(intent, holder, state, movingUp)));
    }

    private static void StartSteadyBobTween(NIntent intent, Control holder, IntentAnimState state, bool isAtTop)
    {
        if (!GodotObject.IsInstanceValid(intent) || !GodotObject.IsInstanceValid(holder))
            return;
        state.BobTween?.Kill();
        state.BobTween = intent.CreateTween().SetLoops();
        if (isAtTop)
        {
            state.BobTween.TweenProperty(holder, "position:y", GetBobBottomY(), 1.0).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            state.BobTween.TweenProperty(holder, "position:y", GetBobTopY(), 1.0).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
        }
        else
        {
            state.BobTween.TweenProperty(holder, "position:y", GetBobTopY(), 1.0).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
            state.BobTween.TweenProperty(holder, "position:y", GetBobBottomY(), 1.0).SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
        }
    }

    private static float GetBobPositionY(float phase) => -(Mathf.Sin(phase) * BobDistance + BobOffset);

    private static float GetBobTopY() => -(BobDistance + BobOffset);

    private static float GetBobBottomY() => BobDistance - BobOffset;

    private static double GetCurrentTimeSeconds() => Time.GetTicksUsec() * 1E-06;


    private static IntentAnimState GetState(NIntent intent, bool create = true)
    {
        return States.TryGetValue(intent, out var state) ? state : create ? States.GetOrCreateValue(intent) : null;
    }



    private sealed class IntentAnimState
    {
        public Tween BobTween;
        public string AnimationName;
        public int? AnimationFrame;
        public double AnimationStartTimeSeconds;
        public Texture2D[] Frames;
    }
}
