using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

internal static class RenderWarmupPatches
{
    // Both entry points run on Godot's main thread. Serialize them if the user
    // changes scenes while the deferred startup pass is still yielding.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static bool IsEnabled => AndroidSettingsBridge.GetBool("preload_enabled", true)
        && AndroidSettingsBridge.GetBool("preload_render_warmup_enabled", false);

    internal static async Task RunStartupAsync(Node owner)
    {
        await Gate.WaitAsync();
        try
        {
            if (!IsEnabled || !Alive(owner))
                return;
            ulong started = Time.GetTicksMsec();
            using var warmup = new RenderReplicaWarmup(owner, isActive: () => IsEnabled);
            var paths = new SortedSet<string>(AndroidStartupLoadingScreen.GetWarmupScenePathsForMode("full"), StringComparer.Ordinal);
            CollectScenes("res://scenes/vfx", paths);
            int loaded = 0, failed = 0, missing = 0;
            foreach (string path in paths)
            {
                if (!IsEnabled || !Alive(owner))
                    break;
                Node source = null;
                try
                {
                    if (!ResourceLoader.Exists(path))
                    {
                        missing++;
                        continue;
                    }
                    PackedScene scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Reuse);
                    if (scene == null)
                    {
                        failed++;
                        continue;
                    }
                    // Constructors/local-resource setup may still execute here;
                    // no original node ever enters the SceneTree or runs Ready.
                    source = scene.Instantiate();
                    loaded++;
                    await warmup.WarmAsync(source);
                }
                catch (Exception exception)
                {
                    failed++;
                    PatchHelper.Log($"Render replica scene extraction failed: path={path} error={exception.GetType().Name}: {exception.Message}");
                }
                finally
                {
                    if (source != null && GodotObject.IsInstanceValid(source))
                        source.Free();
                }
                if (Alive(owner))
                {
                    var tree = owner.GetTree();
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                }
            }
            // Also cover currently mounted menu visuals, without running their
            // callbacks again. Creature/background visuals are covered per room.
            if (Alive(owner) && IsEnabled)
                await warmup.WarmAsync(owner);
            warmup.LogSummary("startup-static-vfx-and-menu");
            PatchHelper.Log($"Render replica scenes: discovered={paths.Count} loaded={loaded} missing={missing} failed={failed} elapsed_ms={Time.GetTicksMsec() - started}; runtime-generated visuals/animation states are not certified.");
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task RunCombatAsync(Node room, Func<bool> isActive)
    {
        await Gate.WaitAsync();
        try
        {
            if (!IsEnabled || !Alive(room) || !isActive())
                return;
            using var warmup = new RenderReplicaWarmup(room, isActive: () => IsEnabled && isActive());
            // One read-only snapshot scan. No recurring scene-tree traversal or
            // mutation of live emission, visibility, materials or animations.
            await warmup.WarmAsync(room);
            warmup.LogSummary("combat-mounted-visuals");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool Alive(Node node) => node != null && GodotObject.IsInstanceValid(node)
        && node.IsInsideTree() && !node.IsQueuedForDeletion();

    private static void CollectScenes(string path, ISet<string> scenes)
    {
        using var directory = DirAccess.Open(path);
        if (directory == null)
            return;
        foreach (string file in directory.GetFiles())
        {
            string logical = file.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) ? file[..^6] : file;
            if (logical.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || logical.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
                scenes.Add(path + "/" + logical);
        }
        foreach (string child in directory.GetDirectories())
            if (!child.StartsWith(".", StringComparison.Ordinal))
                CollectScenes(path + "/" + child, scenes);
    }
}
