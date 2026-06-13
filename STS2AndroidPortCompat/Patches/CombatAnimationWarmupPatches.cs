using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class CombatAnimationWarmupPatches
{
    private const string ModeOff = "off";
    private const string ModeSafe = "safe";
    private const string ModeAll = "all";
    private const int MaxTriggersPerCreature = 64;
    private const int MaxClipsPerCreature = 128;

    private static readonly ConditionalWeakTable<NCombatRoom, WarmupState> RoomStates = new();
    private static readonly FieldInfo SpineAnimatorField = AccessTools.Field(typeof(NCreature), "_spineAnimator");
    private static readonly FieldInfo AnimatorAnyStateField = AccessTools.Field(typeof(CreatureAnimator), "_anyState");
    private static readonly FieldInfo AnimatorCurrentStateField = AccessTools.Field(typeof(CreatureAnimator), "_currentState");
    private static readonly FieldInfo AnimStateBranchesField = AccessTools.Field(typeof(AnimState), "_branchedStates");

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NCombatRoom), "_Ready", postfix: PatchHelper.Method(typeof(CombatAnimationWarmupPatches), nameof(CombatRoomReadyPostfix)));
    }

    public static void CombatRoomReadyPostfix(NCombatRoom __instance)
    {
        try
        {
            if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase) || __instance == null)
                return;
            string mode = GetWarmupMode();
            bool hitEffectWarmup = IsCombatHitEffectWarmupEnabled();
            if (mode == ModeOff && !hitEffectWarmup)
                return;
            if (__instance.Mode != CombatRoomMode.ActiveCombat)
                return;

            var state = RoomStates.GetOrCreateValue(__instance);
            if (state.Started)
                return;
            state.Started = true;

            Callable.From(() =>
            {
                _ = WarmupCombatAnimationsAsync(__instance, mode, GetWarmupFrames(), hitEffectWarmup);
            }).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat animation warmup scheduling failed: {exception.Message}");
        }
    }

    private static async Task WarmupCombatAnimationsAsync(NCombatRoom room, string mode, int frames, bool hitEffectWarmup)
    {
        ulong started = Time.GetTicksMsec();
        int warmedCreatures = 0;
        int warmedTriggers = 0;
        int warmedClips = 0;
        int failedTriggers = 0;
        int failedClips = 0;
        int warmedHitEffects = 0;
        int warmedHitAudio = 0;
        int failedHitEffects = 0;
        try
        {
            await WaitForFramesAsync(2);
            if (!IsValid(room))
                return;

            var creatures = room.CreatureNodes
                .Where(IsWarmableCreature)
                .ToArray();
            if (creatures.Length == 0)
                return;

            PatchHelper.Log($"Combat animation warmup begin: mode={mode} creatures={creatures.Length:N0} frames={frames:N0} hit_effects={hitEffectWarmup}.");
            ColorRect warmupMask = CreateWarmupMask(room);
            if (warmupMask != null)
                await WaitForFramesAsync(1);
            try
            {
                if (mode != ModeOff)
                {
                    foreach (NCreature creature in creatures)
                    {
                        if (!IsValid(creature))
                            continue;
                        string originalAnimation = TryGetCurrentAnimationName(creature);
                        AnimState originalState = TryGetCurrentAnimatorState(creature);
                        Color originalModulate = creature.Modulate;
                        try
                        {
                            creature.Modulate = WithAlpha(originalModulate, Math.Min(originalModulate.A, 0.02f));
                            string[] triggerNames = CollectWarmupTriggerNames(creature, mode);
                            string[] animationNames = CollectWarmupAnimationNames(creature, mode);
                            if (triggerNames.Length == 0 && animationNames.Length == 0)
                                continue;
                            warmedCreatures++;
                            if (IsPreloadDebugEnabled())
                                PatchHelper.Log($"Combat animation warmup creature={DescribeCreature(creature)} triggers=[{DescribeList(triggerNames)}] clips=[{DescribeList(animationNames)}].");
                            foreach (string triggerName in triggerNames)
                            {
                                if (await WarmAnimationTriggerAsync(creature, triggerName, frames))
                                    warmedTriggers++;
                                else
                                    failedTriggers++;
                            }
                            if (triggerNames.Length > 0)
                            {
                                RestoreAnimation(creature, originalAnimation, originalState);
                                await WaitForFramesAsync(1);
                            }
                            foreach (string animationName in animationNames)
                            {
                                if (await WarmAnimationClipAsync(creature, animationName, frames))
                                    warmedClips++;
                                else
                                    failedClips++;
                            }
                        }
                        catch (Exception exception)
                        {
                            failedClips++;
                            PatchHelper.Log($"Combat animation warmup skipped creature={DescribeCreature(creature)}: {exception.GetType().Name}: {exception.Message}");
                        }
                        finally
                        {
                            RestoreAnimation(creature, originalAnimation, originalState);
                            creature.Modulate = originalModulate;
                            await WaitForFramesAsync(1);
                        }
                    }
                }

                if (hitEffectWarmup)
                {
                    (warmedHitEffects, failedHitEffects, warmedHitAudio) = await WarmCombatHitEffectsAsync(room, creatures, Math.Max(frames, 6));
                }
            }
            finally
            {
                RemoveWarmupMask(warmupMask);
                if (warmupMask != null)
                    await WaitForFramesAsync(1);
            }

            PatchHelper.Log($"Combat animation warmup complete: mode={mode} creatures={warmedCreatures:N0}/{creatures.Length:N0} triggers={warmedTriggers:N0} trigger_failed={failedTriggers:N0} clips={warmedClips:N0} clip_failed={failedClips:N0} hit_effects={warmedHitEffects:N0} hit_audio={warmedHitAudio:N0} hit_effect_failed={failedHitEffects:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat animation warmup failed: {exception}");
        }
    }

    private static async Task<(int Warmed, int Failed, int Audio)> WarmCombatHitEffectsAsync(NCombatRoom room, IReadOnlyList<NCreature> creatures, int frames)
    {
        int warmed = 0;
        int failed = 0;
        int warmedAudio = 0;
        var nodesToFree = new List<Node>();
        ulong started = Time.GetTicksMsec();
        try
        {
            if (!IsValid(room?.CombatVfxContainer))
                return (0, 0, 0);

            NCreature[] targets = creatures
                .Where(creature => IsValid(creature) && creature.Entity != null && creature.Entity.IsAlive)
                .OrderBy(creature => creature.Entity.IsPlayer ? 1 : 0)
                .Take(4)
                .ToArray();
            if (targets.Length == 0)
                targets = creatures.Where(IsValid).Take(2).ToArray();

            PatchHelper.Log($"Combat hit-effect warmup begin: targets={targets.Length:N0} frames={frames:N0}.");
            warmedAudio += WarmCommonCombatHitAudio();
            foreach (NCreature target in targets)
            {
                try
                {
                    if (!IsValid(target))
                        continue;
                    Vector2 position = target.VfxSpawnPosition;
                    int visualBefore = nodesToFree.Count;
                    AddWarmupNode(room.CombatVfxContainer, NDamageNumVfx.Create(target.Entity, 6, requireInteractable: false) ?? NDamageNumVfx.Create(position, 6), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, NDamageNumVfx.Create(position + new Vector2(24f, -8f), 1234567890), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, NHitSparkVfx.Create(target.Entity, requireInteractable: false), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, CreatePositionedVfx("vfx/vfx_attack_slash", target.GlobalPosition), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, CreatePositionedVfx("vfx/vfx_attack_blunt", position + new Vector2(16f, 0f)), nodesToFree);
                    warmed += nodesToFree.Count - visualBefore;
                    warmedAudio += WarmCombatHitAudio(target.Entity);
                }
                catch (Exception exception)
                {
                    failed++;
                    PatchHelper.Log($"Combat hit-effect warmup skipped target={DescribeCreature(target)}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            await WaitForFramesAsync(Math.Max(12, Math.Min(30, frames)));
        }
        catch (Exception exception)
        {
            failed++;
            PatchHelper.Log($"Combat hit-effect warmup failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            ConcealWarmupNodes(nodesToFree);
            _ = FreeWarmupNodesAfterDelayAsync(nodesToFree.ToArray(), 180);
            await WaitForFramesAsync(1);
        }

        PatchHelper.Log($"Combat hit-effect warmup complete: warmed={warmed:N0} audio={warmedAudio:N0} failed={failed:N0} nodes={nodesToFree.Count:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
        return (warmed, failed, warmedAudio);
    }

    private static int WarmCombatHitAudio(Creature creature)
    {
        try
        {
            var audio = NAudioManager.Instance;
            MonsterModel monster = creature?.Monster;
            if (audio == null || monster == null)
                return 0;

            int warmed = 0;
            if (!string.IsNullOrWhiteSpace(monster.TakeDamageSfx))
            {
                audio.PlayOneShot(monster.TakeDamageSfx, new Dictionary<string, float> { { "EnemyImpact_Intensity", 2f } }, 0f);
                warmed++;
            }
            if (monster.HasHurtSfx && !string.IsNullOrWhiteSpace(monster.HurtSfx))
            {
                audio.PlayOneShot(monster.HurtSfx, 0f);
                warmed++;
            }
            return warmed;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat hit audio warmup skipped creature={creature}: {exception.GetType().Name}: {exception.Message}");
            return 0;
        }
    }

    private static int WarmCommonCombatHitAudio()
    {
        try
        {
            var audio = NAudioManager.Instance;
            if (audio == null)
                return 0;

            var impactParameters = new Dictionary<string, float> { { "EnemyImpact_Intensity", 2f } };
            string[] paths =
            {
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_armor",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_armor_big",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_fur",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_insect",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_magic",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_plant",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_slime",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_stone",
            };

            int warmed = 0;
            foreach (string path in paths)
            {
                audio.PlayOneShot(path, impactParameters, 0f);
                warmed++;
            }
            audio.PlayOneShot("event:/sfx/block_break", 0f);
            return warmed + 1;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Common combat hit audio warmup skipped: {exception.GetType().Name}: {exception.Message}");
            return 0;
        }
    }

    private static Node2D CreatePositionedVfx(string path, Vector2 position)
    {
        string scenePath = SceneHelper.GetScenePath(path);
        Node2D node = PreloadManager.Cache.GetScene(scenePath).Instantiate<Node2D>(PackedScene.GenEditState.Disabled);
        TryApplyMobileShaderCompatibility(node);
        node.GlobalPosition = position;
        return node;
    }

    private static void TryApplyMobileShaderCompatibility(Node node)
    {
        try
        {
            var type = typeof(PreloadManager).Assembly.GetType("MegaCrit.Sts2.Core.Helpers.MobileShaderCompatibility");
            var method = type?.GetMethod("ApplyIfEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Node) }, null);
            method?.Invoke(null, new object[] { node });
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat hit-effect shader compatibility skipped: {exception.Message}");
        }
    }

    private static void AddWarmupNode(Node parent, Node node, ICollection<Node> nodesToFree)
    {
        if (!IsValid(parent) || node == null)
            return;
        parent.AddChildSafely(node);
        nodesToFree.Add(node);
    }

    private static void FreeWarmupNode(Node node)
    {
        try
        {
            if (!IsValid(node))
                return;
            if (node.IsInsideTree())
                node.QueueFree();
            else
                node.Free();
        }
        catch
        {
        }
    }

    private static void ConcealWarmupNodes(IEnumerable<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            try
            {
                if (IsValid(node) && node is CanvasItem canvasItem)
                {
                    canvasItem.Modulate = WithAlpha(canvasItem.Modulate, 0f);
                    canvasItem.Visible = false;
                }
            }
            catch
            {
            }
        }
    }

    private static async Task FreeWarmupNodesAfterDelayAsync(Node[] nodes, int frames)
    {
        try
        {
            await WaitForFramesAsync(frames);
            foreach (Node node in nodes)
                FreeWarmupNode(node);
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Delayed combat hit-effect cleanup skipped: {exception.Message}");
        }
    }

    private static ColorRect CreateWarmupMask(NCombatRoom room)
    {
        try
        {
            if (!IsValid(room))
                return null;

            var mask = new ColorRect
            {
                Name = "AndroidCombatAnimationWarmupMask",
                LayoutMode = 1,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                Color = Colors.Black,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 4096,
                ZAsRelative = false,
            };
            room.AddChild(mask);
            room.MoveChild(mask, room.GetChildCount() - 1);
            return mask;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation warmup mask unavailable: {exception.Message}");
            return null;
        }
    }

    private static void RemoveWarmupMask(ColorRect mask)
    {
        try
        {
            if (IsValid(mask))
                mask.QueueFree();
        }
        catch
        {
        }
    }

    private static async Task<bool> WarmAnimationTriggerAsync(NCreature creature, string triggerName, int frames)
    {
        if (!IsValid(creature) || string.IsNullOrWhiteSpace(triggerName))
            return false;
        try
        {
            creature.SetAnimationTrigger(triggerName);
            MegaTrackEntry track = creature.SpineAnimation.GetCurrentTrack();
            try
            {
                track?.SetMixDuration(0f);
            }
            catch
            {
            }

            float duration = 0f;
            try
            {
                duration = Math.Max(0f, track?.GetAnimationEnd() ?? 0f);
            }
            catch
            {
                duration = 0f;
            }

            int samples = Math.Max(1, frames);
            for (int i = 0; i < samples; i++)
            {
                if (track != null && duration > 0.001f)
                {
                    float ratio = samples == 1 ? 0.05f : (float)i / Math.Max(1, samples - 1);
                    float trackTime = Math.Min(duration - 0.001f, Math.Max(0f, duration * ratio));
                    track.SetTrackTime(trackTime);
                }
                ApplyCurrentSpineState(creature);
                await WaitForFramesAsync(1);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation warmup trigger skipped creature={DescribeCreature(creature)} trigger={triggerName}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static async Task<bool> WarmAnimationClipAsync(NCreature creature, string animationName, int frames)
    {
        if (!IsValid(creature) || string.IsNullOrWhiteSpace(animationName))
            return false;
        try
        {
            MegaTrackEntry track = creature.SpineAnimation.SetAnimation(animationName, IsLoopingAnimationName(animationName), 0);
            if (track == null)
                return false;
            try
            {
                track.SetMixDuration(0f);
            }
            catch
            {
            }

            float duration = 0f;
            try
            {
                duration = Math.Max(0f, track.GetAnimationEnd());
            }
            catch
            {
                duration = 0f;
            }

            int samples = Math.Max(1, frames);
            for (int i = 0; i < samples; i++)
            {
                if (duration > 0.001f)
                {
                    float ratio = samples == 1 ? 0.05f : (float)i / Math.Max(1, samples - 1);
                    float trackTime = Math.Min(duration - 0.001f, Math.Max(0f, duration * ratio));
                    track.SetTrackTime(trackTime);
                }
                ApplyCurrentSpineState(creature);
                await WaitForFramesAsync(1);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation warmup clip skipped creature={DescribeCreature(creature)} animation={animationName}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static void ApplyCurrentSpineState(NCreature creature)
    {
        try
        {
            MegaAnimationState state = creature.SpineAnimation.GetAnimationState();
            MegaSkeleton skeleton = creature.Visuals?.SpineBody?.GetSkeleton();
            if (state != null && skeleton != null)
            {
                state.Update(0f);
                state.Apply(skeleton);
            }
        }
        catch
        {
        }
    }

    private static string[] CollectWarmupTriggerNames(NCreature creature, string mode)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CreatureAnimator animator = TryGetSpineAnimator(creature);
        if (animator == null)
            return Array.Empty<string>();

        CollectTriggerNamesFromState(AnimatorAnyStateField?.GetValue(animator) as AnimState, names, new HashSet<AnimState>());
        CollectTriggerNamesFromState(AnimatorCurrentStateField?.GetValue(animator) as AnimState, names, new HashSet<AnimState>());
        return names
            .Where(name => IsWarmableTriggerName(creature, name, mode))
            .OrderBy(GetTriggerWarmupPriority)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(MaxTriggersPerCreature)
            .ToArray();
    }

    private static void CollectTriggerNamesFromState(AnimState state, ISet<string> names, ISet<AnimState> visited)
    {
        if (state == null || !visited.Add(state))
            return;

        if (AnimStateBranchesField?.GetValue(state) is IDictionary branches)
        {
            foreach (object key in branches.Keys)
            {
                if (key is string name && !string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            foreach (object value in branches.Values)
            {
                if (value is not IEnumerable branchList)
                    continue;
                foreach (object branch in branchList)
                {
                    AnimState child = TryGetBranchState(branch);
                    if (child != null)
                        CollectTriggerNamesFromState(child, names, visited);
                }
            }
        }

        CollectTriggerNamesFromState(state.NextState, names, visited);
    }

    private static AnimState TryGetBranchState(object branch)
    {
        try
        {
            return branch?.GetType()
                .GetField("state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(branch) as AnimState;
        }
        catch
        {
            return null;
        }
    }

    private static CreatureAnimator TryGetSpineAnimator(NCreature creature)
    {
        try
        {
            return SpineAnimatorField?.GetValue(creature) as CreatureAnimator;
        }
        catch
        {
            return null;
        }
    }

    private static string[] CollectWarmupAnimationNames(NCreature creature, string mode)
    {
        var names = CollectAllAnimationNames(creature);
        if (mode == ModeSafe)
            names = names.Where(name => IsSafeCombatAnimationName(creature, name));
        return names
            .Where(name => IsUsableAnimationName(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GetAnimationWarmupPriority)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(MaxClipsPerCreature)
            .ToArray();
    }

    private static IEnumerable<string> CollectAllAnimationNames(NCreature creature)
    {
        MegaSprite sprite = creature.Visuals?.SpineBody;
        MegaSkeleton skeleton = sprite?.GetSkeleton();
        MegaSkeletonDataResource data = skeleton?.GetData();
        if (data == null)
            yield break;

        Godot.Collections.Array<GodotObject> animations;
        try
        {
            animations = (Godot.Collections.Array<GodotObject>)data.BoundObject.Call("get_animations");
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation list unavailable creature={DescribeCreature(creature)}: {exception.Message}");
            yield break;
        }

        foreach (GodotObject animation in animations)
        {
            string name = null;
            try
            {
                name = animation?.Call("get_name").AsString();
            }
            catch
            {
                name = null;
            }
            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
        }
    }

    private static bool IsWarmableCreature(NCreature creature)
    {
        try
        {
            return IsValid(creature)
                && creature.HasSpineAnimation
                && creature.Visuals?.SpineBody != null
                && creature.Entity != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeCombatAnimationName(NCreature creature, string name)
    {
        if (!IsUsableAnimationName(name))
            return false;
        if (IsDangerousAnimationName(name))
            return false;
        string lower = name.ToLowerInvariant();
        if (creature.Entity?.IsPlayer == true)
            return ContainsAny(lower, "idle", "attack", "cast", "hurt", "hit", "shiv", "relaxed", "block");
        return ContainsAny(lower,
            "idle", "attack", "cast", "hurt", "hit", "buff", "debuff", "heal", "rally",
            "bite", "slash", "stab", "smash", "swipe", "throw", "poke", "spit", "vomit",
            "hug", "chomp", "flail", "ram", "breaker", "bomb", "laser", "grenade",
            "heavy", "uppercut", "sharpen", "vines", "thrash");
    }

    private static bool IsWarmableTriggerName(NCreature creature, string name, string mode)
    {
        if (!IsUsableTriggerName(name) || IsDangerousAnimationName(name))
            return false;
        if (mode == ModeSafe)
            return IsSafeCombatTriggerName(creature, name);
        return true;
    }

    private static bool IsSafeCombatTriggerName(NCreature creature, string name)
    {
        string lower = name.ToLowerInvariant();
        if (creature.Entity?.IsPlayer == true)
            return ContainsAny(lower, "idle", "attack", "cast", "hit", "shiv", "relaxed");
        return ContainsAny(lower,
            "idle", "attack", "cast", "hit", "buff", "debuff", "heal", "rally",
            "bite", "slash", "stab", "smash", "swipe", "throw", "poke", "spit", "vomit",
            "hug", "chomp", "flail", "ram", "breaker", "bomb", "laser", "grenade",
            "heavy", "uppercut", "sharpen", "vines", "thrash", "charge");
    }

    private static bool IsUsableTriggerName(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, "Dead", StringComparison.Ordinal)
            && !string.Equals(name, "Revive", StringComparison.Ordinal);
    }

    private static bool IsUsableAnimationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string lower = name.ToLowerInvariant();
        return !lower.Equals("empty", StringComparison.Ordinal)
            && !lower.EndsWith("/empty", StringComparison.Ordinal)
            && !lower.Equals("_tracks/empty", StringComparison.Ordinal)
            && !lower.Equals("tracks/empty", StringComparison.Ordinal);
    }

    private static bool IsDangerousAnimationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string lower = name.ToLowerInvariant();
        return ContainsAny(lower, "die", "dead", "death", "flee", "escape", "spawn", "summon", "revive", "hatch", "burrow", "sleep", "wake", "intro");
    }

    private static int GetTriggerWarmupPriority(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("idle"))
            return 0;
        if (lower.Contains("attack"))
            return 1;
        if (lower.Contains("shiv"))
            return 2;
        if (lower.Contains("cast"))
            return 3;
        if (lower.Contains("hit"))
            return 4;
        if (lower.Contains("relaxed"))
            return 5;
        return 50;
    }

    private static int GetAnimationWarmupPriority(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("idle"))
            return 0;
        if (lower.Contains("attack"))
            return 1;
        if (lower.Contains("shiv"))
            return 2;
        if (lower.Contains("cast"))
            return 3;
        if (lower.Contains("hurt") || lower.Contains("hit"))
            return 4;
        if (lower.Contains("die") || lower.Contains("dead"))
            return 90;
        return 50;
    }

    private static void RestoreAnimation(NCreature creature, string originalAnimation, AnimState originalState)
    {
        try
        {
            if (!IsValid(creature))
                return;
            string animationToRestore = !string.IsNullOrWhiteSpace(originalAnimation) ? originalAnimation : originalState?.Id;
            if (!string.IsNullOrWhiteSpace(animationToRestore))
                creature.SpineAnimation.SetAnimation(animationToRestore, IsLoopingAnimationName(animationToRestore), 0);
            else
                creature.SetAnimationTrigger("Idle");
            RestoreCurrentAnimatorState(creature, originalState);
            ApplyCurrentSpineState(creature);
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation restore skipped creature={DescribeCreature(creature)}: {exception.Message}");
        }
    }

    private static AnimState TryGetCurrentAnimatorState(NCreature creature)
    {
        try
        {
            CreatureAnimator animator = TryGetSpineAnimator(creature);
            return AnimatorCurrentStateField?.GetValue(animator) as AnimState;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreCurrentAnimatorState(NCreature creature, AnimState originalState)
    {
        if (originalState == null)
            return;
        try
        {
            CreatureAnimator animator = TryGetSpineAnimator(creature);
            if (animator != null)
                AnimatorCurrentStateField?.SetValue(animator, originalState);
        }
        catch
        {
        }
    }

    private static string TryGetCurrentAnimationName(NCreature creature)
    {
        try
        {
            MegaTrackEntry track = creature.SpineAnimation.GetCurrentTrack();
            GodotObject animation = track?.BoundObject.Call("get_animation").AsGodotObject();
            return animation?.Call("get_name").AsString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLoopingAnimationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string lower = name.ToLowerInvariant();
        return lower.Contains("loop") || lower.Contains("idle") || lower.Contains("hover") || lower.Contains("writhe");
    }

    private static async Task WaitForFramesAsync(int frames)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            await Task.Yield();
            return;
        }
        for (int i = 0; i < Math.Max(1, frames); i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        return new Color(color.R, color.G, color.B, Math.Max(0f, Math.Min(1f, alpha)));
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsValid(GodotObject value)
    {
        return value != null && GodotObject.IsInstanceValid(value);
    }

    private static string GetWarmupMode()
    {
        if (!AndroidSettingsBridge.GetBool("preload_enabled", true))
            return ModeOff;
        string value = AndroidSettingsBridge.GetString("preload_combat_animation_warmup_mode", ModeOff).Trim().ToLowerInvariant();
        return value switch
        {
            "safe" or "current_room_safe" or "room_safe" => ModeSafe,
            "all" or "full" or "current_room_all" or "room_all" => ModeAll,
            _ => ModeOff,
        };
    }

    private static int GetWarmupFrames()
    {
        if (!AndroidSettingsBridge.GetBool("preload_enabled", true))
            return 0;
        int frames = AndroidSettingsBridge.GetInt("preload_combat_animation_warmup_frames", 1);
        return Math.Max(1, Math.Min(12, frames));
    }

    private static bool IsCombatHitEffectWarmupEnabled()
    {
        return AndroidSettingsBridge.GetBool("preload_enabled", true)
            && AndroidSettingsBridge.GetBool("preload_combat_hit_effect_warmup_enabled", false);
    }

    private static bool IsPreloadDebugEnabled() => AndroidSettingsBridge.GetBool("preload_debug_enabled", false);

    private static string DescribeCreature(NCreature creature)
    {
        try
        {
            return creature?.Entity?.ToString() ?? creature?.Name.ToString() ?? "<null>";
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string DescribeList(IReadOnlyList<string> values)
    {
        if (values == null || values.Count == 0)
            return "";
        const int max = 16;
        string joined = string.Join(",", values.Take(max));
        if (values.Count > max)
            joined += $",+{values.Count - max:N0}";
        return joined;
    }

    private sealed class WarmupState
    {
        public bool Started;
    }
}
