using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public partial class AndroidStartupLoadingScreen : Control
{
    private const float SceneWarmupWeight = 0.1f;

    private static readonly string[] HighPriorityWarmupPaths =
    {
        "res://scenes/vfx/hit_spark_vfx.tscn",
        "res://scenes/vfx/vfx_damage_num.tscn",
        "res://scenes/vfx/vfx_attack_slash.tscn",
        "res://scenes/vfx/vfx_attack_blunt.tscn",
        "res://scenes/vfx/vfx_block.tscn",
        "res://scenes/vfx/vfx_slime_impact.tscn",
        "res://scenes/vfx/vfx_starry_impact.tscn",
        "res://scenes/vfx/thin_slice_vfx.tscn",
        "res://scenes/vfx/stab_vfx.tscn",
        "res://scenes/vfx/vfx_shiv_throw.tscn",
        "res://scenes/vfx/vfx_dagger_throw.tscn",
        "res://scenes/vfx/vfx_dagger_spray.tscn",
        "res://scenes/vfx/vfx_dagger_spray_flurry.tscn",
        "res://scenes/vfx/vfx_dagger_spray_impact.tscn",
        "res://scenes/vfx/vfx_poison_impact.tscn",
        "res://scenes/vfx/vfx_smoke_puff.tscn",
        "res://scenes/vfx/power_applied_vfx.tscn",
    };

    private static readonly HashSet<string> TreeWarmupEligiblePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "res://scenes/vfx/vfx_attack_slash.tscn",
        "res://scenes/vfx/vfx_attack_blunt.tscn",
        "res://scenes/vfx/vfx_block.tscn",
        "res://scenes/vfx/vfx_slime_impact.tscn",
        "res://scenes/vfx/vfx_starry_impact.tscn",
        "res://scenes/vfx/thin_slice_vfx.tscn",
        "res://scenes/vfx/stab_vfx.tscn",
        "res://scenes/vfx/vfx_shiv_throw.tscn",
        "res://scenes/vfx/vfx_dagger_throw.tscn",
        "res://scenes/vfx/vfx_dagger_spray.tscn",
        "res://scenes/vfx/vfx_dagger_spray_flurry.tscn",
        "res://scenes/vfx/vfx_dagger_spray_impact.tscn",
        "res://scenes/vfx/vfx_poison_impact.tscn",
        "res://scenes/vfx/vfx_smoke_puff.tscn",
    };

    private static bool _combatHotPathsPrewarmed;

    private Label _titleLabel;
    private Label _detailsLabel;
    private ColorRect _progressFrame;
    private ColorRect _progressFill;

    private bool _uiBuilt;

    public override void _Ready()
    {
        EnsureUiBuilt();
    }

    public Task PresentAsync()
    {
        EnsureUiBuilt();
        return EnsureScreenIsVisibleAsync();
    }

    public void SetStatus(string title, string details, float progress)
    {
        EnsureUiBuilt();
        _titleLabel.Text = title;
        _detailsLabel.Text = details;
        UpdateOverallProgress(progress);
    }

    public async Task RunWarmup(AssetLoadingSession session, bool prewarmCombatCode = true, string vfxMode = "full")
    {
        EnsureUiBuilt();
        UpdateSessionProgress(session);
        while (session != null && !session.IsCompleted)
        {
            UpdateSessionProgress(session);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        UpdateSessionProgress(session);
        if (prewarmCombatCode)
        {
            _titleLabel.Text = "Optimizing combat code...";
            _detailsLabel.Text = "Preparing first attack hot paths";
            UpdateOverallProgress(0.88f);
            PrewarmCombatHotPaths();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await WarmupCriticalScenesAsync(this, vfxMode);
        _titleLabel.Text = "Startup optimization complete";
        _detailsLabel.Text = "Launching main menu";
        UpdateOverallProgress(1f);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void EnsureUiBuilt()
    {
        if (_uiBuilt)
            return;
        BuildUi();
        ConfigureLabel(_titleLabel, 42, new Color(0.97f, 0.98f, 1f));
        ConfigureLabel(_detailsLabel, 22, new Color(0.77f, 0.84f, 0.95f));
        _titleLabel.Text = "Optimizing startup...";
        _detailsLabel.Text = "Preparing resource warmup";
        UpdateOverallProgress(0f);
        _uiBuilt = true;
    }

    private void BuildUi()
    {
        ZIndex = 1000;
        ZAsRelative = false;
        LayoutMode = 1;
        AnchorRight = 1f;
        AnchorBottom = 1f;
        MouseFilter = MouseFilterEnum.Ignore;

        AddChild(new ColorRect
        {
            Name = "Background",
            LayoutMode = 1,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = MouseFilterEnum.Ignore,
            Color = new Color(0.101961f, 0.14902f, 0.278431f, 1f),
        });

        AddChild(new ColorRect
        {
            Name = "TopAccent",
            LayoutMode = 1,
            AnchorRight = 1f,
            OffsetBottom = 6f,
            MouseFilter = MouseFilterEnum.Ignore,
            Color = new Color(0.898039f, 0.470588f, 0.243137f, 1f),
        });

        AddChild(new ColorRect
        {
            Name = "BottomAccent",
            LayoutMode = 1,
            AnchorTop = 1f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetTop = -6f,
            MouseFilter = MouseFilterEnum.Ignore,
            Color = new Color(0.231373f, 0.564706f, 0.858824f, 1f),
        });

        var centerContainer = new CenterContainer
        {
            Name = "CenterContainer",
            LayoutMode = 1,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(centerContainer);

        var vbox = new VBoxContainer
        {
            Name = "VBox",
            CustomMinimumSize = new Vector2(920, 220),
            LayoutMode = 2,
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddThemeConstantOverride("separation", 18);
        centerContainer.AddChild(vbox);

        _titleLabel = new Label
        {
            Name = "TitleLabel",
            UniqueNameInOwner = true,
            LayoutMode = 2,
            Text = "Optimizing startup...",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddChild(_titleLabel);

        _detailsLabel = new Label
        {
            Name = "DetailsLabel",
            UniqueNameInOwner = true,
            LayoutMode = 2,
            Text = "Preparing resource warmup",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddChild(_detailsLabel);

        var progressOuter = new PanelContainer
        {
            Name = "ProgressOuter",
            LayoutMode = 2,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddChild(progressOuter);

        var progressMargin = new MarginContainer
        {
            Name = "ProgressMargin",
            LayoutMode = 2,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        progressMargin.AddThemeConstantOverride("margin_left", 14);
        progressMargin.AddThemeConstantOverride("margin_top", 14);
        progressMargin.AddThemeConstantOverride("margin_right", 14);
        progressMargin.AddThemeConstantOverride("margin_bottom", 14);
        progressOuter.AddChild(progressMargin);

        _progressFrame = new ColorRect
        {
            Name = "ProgressFrame",
            UniqueNameInOwner = true,
            CustomMinimumSize = new Vector2(760, 28),
            LayoutMode = 2,
            MouseFilter = MouseFilterEnum.Ignore,
            Color = new Color(0.137255f, 0.188235f, 0.266667f, 1f),
        };
        progressMargin.AddChild(_progressFrame);

        _progressFill = new ColorRect
        {
            Name = "ProgressFill",
            UniqueNameInOwner = true,
            LayoutMode = 1,
            OffsetRight = 0f,
            OffsetBottom = 28f,
            MouseFilter = MouseFilterEnum.Ignore,
            Color = new Color(0.929412f, 0.588235f, 0.286275f, 1f),
        };
        _progressFrame.AddChild(_progressFill);

        var hintLabel = new Label
        {
            Name = "HintLabel",
            LayoutMode = 2,
            Modulate = new Color(1f, 1f, 1f, 0.501961f),
            Text = "First startup warmup may take a bit longer on Android",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddChild(hintLabel);
    }

    private async Task EnsureScreenIsVisibleAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SceneTreeTimer timer = GetTree().CreateTimer(0.15);
        await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }

    public static async Task WarmupCriticalScenesAsync(AndroidStartupLoadingScreen loadingScreen, string mode, bool treeWarmup = false, int treeWarmupFrames = 0, bool retainCache = false, string treeWarmupScope = "safe")
    {
        string[] warmupScenePaths = GetWarmupScenePaths(mode);
        if (warmupScenePaths.Length == 0)
        {
            loadingScreen?.UpdateOverallProgress(1f);
            return;
        }

        var tree = (loadingScreen?.GetTree() ?? (Engine.GetMainLoop() as SceneTree));
        Node warmupRoot = treeWarmup ? CreateWarmupRoot(loadingScreen, tree) : null;
        int cachedBefore = CountCached(warmupScenePaths);
        int loaded = 0;
        int instantiated = 0;
        int resourceOnly = 0;
        int treeIneligible = 0;
        int treeWarmed = 0;
        int treeFailed = 0;
        int skipped = 0;
        int failed = 0;
        string normalizedTreeWarmupScope = NormalizeTreeWarmupScope(treeWarmupScope);
        bool treeWarmupAll = string.Equals(normalizedTreeWarmupScope, "all", StringComparison.Ordinal);
        int treeEligible = treeWarmup ? warmupScenePaths.Count(path => IsTreeWarmupEligible(path, normalizedTreeWarmupScope)) : 0;
        ShaderCacheSnapshot shaderCacheBefore = ShaderCacheSnapshot.Capture();
        ulong started = Time.GetTicksMsec();
        PatchHelper.Log($"Android VFX warmup begin: mode={mode} scenes={warmupScenePaths.Length:N0} tree={treeWarmup} tree_scope={normalizedTreeWarmupScope} tree_eligible={treeEligible:N0} frames={treeWarmupFrames} retain_cache={retainCache} cached_before={cachedBefore:N0} shader_cache_before={shaderCacheBefore}.");
        for (int i = 0; i < warmupScenePaths.Length; i++)
        {
            string path = warmupScenePaths[i];
            float progress = 0.9f + (float)i / warmupScenePaths.Length * SceneWarmupWeight;
            if (loadingScreen != null)
            {
                loadingScreen._titleLabel.Text = treeWarmup ? "Rendering combat effects..." : "Compiling combat effects...";
                loadingScreen._detailsLabel.Text = $"{i + 1}/{warmupScenePaths.Length}: {GetSceneDisplayName(path)}";
                loadingScreen.UpdateOverallProgress(progress);
            }

            Node node = null;
            try
            {
                PackedScene packedScene = LoadWarmupScene(path, retainCache);
                if (packedScene == null)
                {
                    skipped++;
                    if (loadingScreen != null)
                        loadingScreen._detailsLabel.Text = $"Skipped: {GetSceneDisplayName(path)}";
                    if (tree != null)
                        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                    else
                        await Task.Yield();
                    continue;
                }
                loaded++;
                node = packedScene.Instantiate<Node>(PackedScene.GenEditState.Disabled);
                instantiated++;
                if (treeWarmup && warmupRoot != null && GodotObject.IsInstanceValid(warmupRoot))
                {
                    if (IsTreeWarmupEligible(path, normalizedTreeWarmupScope))
                    {
                        ConfigureWarmupNode(node, path);
                        try
                        {
                            ulong treeStarted = Time.GetTicksMsec();
                            warmupRoot.AddChild(node);
                            treeWarmed++;
                            int framesToWait = GetTreeWarmupFrameCount(path, treeWarmupFrames);
                            await WaitWarmupFramesAsync(tree, framesToWait);
                            ulong elapsed = Time.GetTicksMsec() - treeStarted;
                            if (elapsed >= 50 && IsPreloadDebugEnabled())
                                PatchHelper.Log($"VFX tree warmup slow path={path} frames={framesToWait} elapsed={elapsed:N0}ms node={node.GetType().FullName}");
                        }
                        catch (Exception exception)
                        {
                            treeFailed++;
                            string nodeType = node != null ? node.GetType().FullName : "<null>";
                            PatchHelper.Log($"VFX tree warmup failed path={path} node={nodeType}: {exception.GetType().Name}: {exception.Message}");
                        }
                        finally
                        {
                            FreeWarmupNode(node);
                            node = null;
                        }
                    }
                    else
                    {
                        treeIneligible++;
                        resourceOnly++;
                        if (treeIneligible <= 20 && IsPreloadDebugEnabled())
                            PatchHelper.Log($"VFX tree warmup resource-only path={path} reason=requires_original_create_or_not_allowlisted scope={normalizedTreeWarmupScope}");
                        FreeWarmupNode(node);
                        node = null;
                    }
                }
                else
                {
                    resourceOnly++;
                    FreeWarmupNode(node);
                    node = null;
                }
            }
            catch (Exception exception)
            {
                failed++;
                PatchHelper.Log($"Startup VFX warmup skipped {path}: {exception.Message}");
                FreeWarmupNode(node);
            }
            if (tree != null)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            else
                await Task.Yield();
        }
        if (warmupRoot != null && GodotObject.IsInstanceValid(warmupRoot))
            warmupRoot.QueueFree();
        loadingScreen?.UpdateOverallProgress(1f);
        int cachedAfter = CountCached(warmupScenePaths);
        ShaderCacheSnapshot shaderCacheAfter = ShaderCacheSnapshot.Capture();
        PatchHelper.Log($"Android VFX warmup complete: mode={mode} scenes={warmupScenePaths.Length:N0} loaded={loaded:N0} instantiated={instantiated:N0} resource_only={resourceOnly:N0} tree_warmed={treeWarmed:N0} tree_ineligible={treeIneligible:N0} tree_failed={treeFailed:N0} skipped={skipped:N0} failed={failed:N0} cached_before={cachedBefore:N0} cached_after={cachedAfter:N0} shader_cache_after={shaderCacheAfter} shader_cache_delta_files={shaderCacheAfter.FileCount - shaderCacheBefore.FileCount:N0} shader_cache_delta_bytes={shaderCacheAfter.TotalBytes - shaderCacheBefore.TotalBytes:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
    }

    private static PackedScene LoadWarmupScene(string path, bool retainCache)
    {
        if (retainCache)
        {
            if (PreloadManager.Cache.ContainsKey(path))
                return PreloadManager.Cache.GetScene(path);
            PackedScene scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Reuse);
            if (scene != null)
                PreloadManager.Cache.SetAsset(path, scene);
            return scene;
        }
        return ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Ignore);
    }

    private static Node CreateWarmupRoot(AndroidStartupLoadingScreen loadingScreen, SceneTree tree)
    {
        try
        {
            Node parent = loadingScreen != null ? loadingScreen : tree?.Root;
            if (parent == null || !GodotObject.IsInstanceValid(parent))
                return null;
            var root = new Control
            {
                Name = "AndroidVfxTreeWarmupRoot",
                LayoutMode = 1,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = -1000,
                ZAsRelative = false,
                ClipContents = false,
            };
            parent.AddChild(root);
            if (loadingScreen != null && GodotObject.IsInstanceValid(loadingScreen))
                loadingScreen.MoveChild(root, 0);
            return root;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"VFX tree warmup root unavailable: {exception.Message}");
            return null;
        }
    }

    private static void ConfigureWarmupNode(Node node, string path)
    {
        if (node == null)
            return;
        node.Name = "AndroidWarmup_" + node.Name;
        node.ProcessMode = ProcessModeEnum.Always;
        if (node is CanvasItem canvasItem)
        {
            canvasItem.Visible = true;
            canvasItem.ZIndex = -1000;
            canvasItem.ZAsRelative = false;
        }
        if (node is Node2D node2D)
        {
            node2D.Position = new Vector2(960f, 540f);
            node2D.Scale = Vector2.One;
        }
        else if (node is Control control)
        {
            control.Position = new Vector2(960f, 540f);
            control.Size = new Vector2(256f, 256f);
            control.MouseFilter = MouseFilterEnum.Ignore;
        }
        ConfigureCombatVfxWarmupNode(node, path);
    }

    private static void ConfigureCombatVfxWarmupNode(Node node, string path)
    {
        try
        {
            if (path != null && (path.Contains("shiv", StringComparison.OrdinalIgnoreCase) || path.Contains("dagger", StringComparison.OrdinalIgnoreCase)))
            {
                InvokeOptionalWarmupMethod(node, "ApplyTint", new object[] { new Color("#47ff7a") });
                InvokeOptionalWarmupMethod(node, "ApplyRotation", new object[] { new Vector2(760f, 540f), new Vector2(1160f, 540f) });
            }
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"VFX warmup node configuration skipped path={path}: {exception.Message}");
        }
    }

    private static void InvokeOptionalWarmupMethod(Node node, string methodName, object[] arguments)
    {
        try
        {
            Type[] argumentTypes = arguments.Select(argument => argument.GetType()).ToArray();
            MethodInfo method = node.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, argumentTypes, null);
            method?.Invoke(node, arguments);
        }
        catch (Exception exception)
        {
            if (exception is TargetInvocationException targetException && targetException.InnerException != null)
                exception = targetException.InnerException;
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"VFX warmup optional call skipped node={node.GetType().Name} method={methodName}: {exception.Message}");
        }
    }

    private static string NormalizeTreeWarmupScope(string scope)
    {
        return string.Equals(scope?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ? "all" : "safe";
    }

    private static bool IsPreloadDebugEnabled() => AndroidSettingsBridge.GetBool("preload_debug_enabled", false);

    private static bool IsTreeWarmupEligible(string path, string scope)
    {
        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            return IsWarmupScenePath(path);
        return TreeWarmupEligiblePaths.Contains(path);
    }

    private static int GetTreeWarmupFrameCount(string path, int configuredFrames)
    {
        int frames = Math.Max(1, configuredFrames);
        if (path != null && (path.Contains("shiv", StringComparison.OrdinalIgnoreCase) || path.Contains("dagger", StringComparison.OrdinalIgnoreCase)))
            frames = Math.Max(frames, 12);
        return Math.Min(frames, 30);
    }

    private static void FreeWarmupNode(Node node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return;
        if (node.IsInsideTree())
            node.QueueFree();
        else
            node.Free();
    }

    private static async Task WaitWarmupFramesAsync(SceneTree tree, int frames)
    {
        if (tree == null)
        {
            await Task.Yield();
            return;
        }
        for (int i = 0; i < frames; i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static int CountCached(IEnumerable<string> paths)
    {
        int count = 0;
        try
        {
            foreach (string path in paths)
            {
                if (PreloadManager.Cache.ContainsKey(path))
                    count++;
            }
        }
        catch
        {
        }
        return count;
    }

    private readonly struct ShaderCacheSnapshot
    {
        public readonly int FileCount;
        public readonly long TotalBytes;
        public readonly string Buckets;

        private ShaderCacheSnapshot(int fileCount, long totalBytes, string buckets)
        {
            FileCount = fileCount;
            TotalBytes = totalBytes;
            Buckets = buckets ?? string.Empty;
        }

        public static ShaderCacheSnapshot Capture()
        {
            try
            {
                string root = Path.Combine(AppPaths.DataDir, "shader_cache");
                if (!Directory.Exists(root))
                    return new ShaderCacheSnapshot(0, 0, "missing");

                int fileCount = 0;
                long totalBytes = 0;
                var bucketCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }

                    string bucket = GetTopLevelShaderCacheBucket(root, file);
                    bucketCounts.TryGetValue(bucket, out int count);
                    bucketCounts[bucket] = count + 1;
                }

                string buckets = string.Join(",", bucketCounts
                    .OrderByDescending(pair => pair.Value)
                    .Take(8)
                    .Select(pair => $"{pair.Key}:{pair.Value:N0}"));
                return new ShaderCacheSnapshot(fileCount, totalBytes, buckets);
            }
            catch (Exception exception)
            {
                return new ShaderCacheSnapshot(0, 0, "error=" + exception.GetType().Name);
            }
        }

        public override string ToString()
        {
            return $"files={FileCount:N0},bytes={TotalBytes:N0},buckets=[{Buckets}]";
        }

        private static string GetTopLevelShaderCacheBucket(string root, string file)
        {
            try
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                int slash = relative.IndexOf('/');
                return slash > 0 ? relative.Substring(0, slash) : "<root>";
            }
            catch
            {
                return "<unknown>";
            }
        }
    }

    private void UpdateSessionProgress(AssetLoadingSession session)
    {
        if (session == null)
        {
            _titleLabel.Text = "Preparing combat warmup...";
            _detailsLabel.Text = "Preparing scene warmup";
            UpdateOverallProgress(0f);
            return;
        }

        LoadingSnapshot snapshot = LoadingSnapshot.From(session);
        _titleLabel.Text = "Loading resources...";
        if (snapshot.TotalToLoad <= 0)
        {
            _titleLabel.Text = "Preparing combat warmup...";
            _detailsLabel.Text = session.IsCompleted ? "Bulk asset preload skipped" : "Preparing scene warmup";
            UpdateOverallProgress(session.IsCompleted ? 0.8f : 0f);
            return;
        }
        int percent = Mathf.RoundToInt(snapshot.Progress * 100f);
        _detailsLabel.Text = $"{snapshot.LoadedCount:N0}/{snapshot.TotalToLoad:N0} resources ({percent}%)";
        UpdateOverallProgress(snapshot.Progress * 0.8f);
    }

    private static string[] GetWarmupScenePaths(string mode)
    {
        return GetWarmupScenePathsForMode(mode);
    }

    public static string[] GetWarmupScenePathsForMode(string mode)
    {
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();
        var paths = new HashSet<string>(HighPriorityWarmupPaths);
        if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string path in GetAllVfxScenePaths())
            {
                if (IsWarmupScenePath(path))
                    paths.Add(path);
            }
        }
        return paths.OrderBy(GetWarmupPriority).ThenBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> GetAllVfxScenePaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectScenePathsRecursive("res://scenes/vfx", paths);
        return paths;
    }

    private static void CollectScenePathsRecursive(string directoryPath, HashSet<string> target)
    {
        using var dir = DirAccess.Open(directoryPath);
        if (dir == null)
            return;
        foreach (string file in dir.GetFiles())
        {
            if (file.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
                target.Add(directoryPath + "/" + file);
        }
        foreach (string directory in dir.GetDirectories())
        {
            if (!string.IsNullOrWhiteSpace(directory))
                CollectScenePathsRecursive(directoryPath + "/" + directory, target);
        }
    }

    private static bool IsWarmupScenePath(string path)
    {
        return path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) && path.Contains("/vfx/");
    }

    private static int GetWarmupPriority(string path)
    {
        int index = Array.IndexOf(HighPriorityWarmupPaths, path);
        return index >= 0 ? index : 1000;
    }

    private void UpdateOverallProgress(float progress)
    {
        if (_progressFrame == null || _progressFill == null)
            return;
        float clamped = Mathf.Clamp(progress, 0f, 1f);
        float width = Mathf.Max(_progressFrame.Size.X, _progressFrame.CustomMinimumSize.X);
        float height = Mathf.Max(_progressFrame.Size.Y, _progressFrame.CustomMinimumSize.Y);
        _progressFill.Size = new Vector2(width * clamped, height);
    }

    private static string GetSceneDisplayName(string path)
    {
        int index = path.LastIndexOf('/');
        string text = index >= 0 ? path.Substring(index + 1) : path;
        if (text.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(0, text.Length - 5);
        return text.Replace('_', ' ');
    }

    private static void ConfigureLabel(Label label, int fontSize, Color color)
    {
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.45f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
    }

    public static void PrewarmCombatHotPaths()
    {
        if (_combatHotPathsPrewarmed)
            return;
        _combatHotPathsPrewarmed = true;
        try
        {
            Type oneTimeInitialization = typeof(MegaCrit.Sts2.Core.Helpers.OneTimeInitialization);
            MethodInfo direct = oneTimeInitialization.GetMethod("PrewarmCombatHotPaths", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (direct != null)
            {
                direct.Invoke(null, null);
                return;
            }

            PrepareMethods("MegaCrit.Sts2.Core.Commands.Builders.AttackCommand", "Execute");
            PrepareMethods("MegaCrit.Sts2.Core.Commands.CreatureCmd", "Damage", "TriggerAnim");
            PrepareMethods("MegaCrit.Sts2.Core.Hooks.Hook", "BeforeAttack", "AfterAttack", "BeforeDamageReceived", "AfterDamageReceived", "ModifyDamage");
            PrepareMethods("MegaCrit.Sts2.Core.Commands.SfxCmd", "Play");
            PrepareMethods("MegaCrit.Sts2.Core.Commands.VfxCmd", "PlayOnCreatureCenter", "PlayOnCreature", "PlayOnSide", "PlayVfx");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NDamageNumVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NHitSparkVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NStabVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NThinSliceVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NPoisonImpactVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NSmokePuffVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NDaggerSprayFlurryVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NDaggerSprayImpactVfx", "Create");
            PrepareMethods("MegaCrit.Sts2.Core.Nodes.Vfx.NShivThrowVfx", "Create", "ApplyTint", "ApplyRotation", "PlaySequence");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat hot-path prewarm skipped: {exception.Message}");
        }
    }

    private static void PrepareMethods(string typeName, params string[] methodNames)
    {
        Type type = typeof(AssetLoadingSession).Assembly.GetType(typeName);
        if (type == null)
            return;
        var names = new HashSet<string>(methodNames);
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!names.Contains(method.Name))
                continue;
            PrepareMethod(method);
        }
    }

    private static void PrepareMethod(MethodInfo method)
    {
        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
        catch
        {
        }
        PrepareAsyncStateMachine(method);
    }

    private static void PrepareAsyncStateMachine(MethodInfo method)
    {
        AsyncStateMachineAttribute attribute = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (attribute == null)
            return;
        MethodInfo moveNext = attribute.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (moveNext == null)
            return;
        try
        {
            RuntimeHelpers.PrepareMethod(moveNext.MethodHandle);
        }
        catch
        {
        }
    }

    private readonly struct LoadingSnapshot
    {
        public readonly int LoadedCount;
        public readonly int TotalToLoad;
        public readonly float Progress;

        private LoadingSnapshot(int loadedCount, int totalToLoad)
        {
            LoadedCount = Math.Max(0, loadedCount);
            TotalToLoad = Math.Max(0, totalToLoad);
            Progress = TotalToLoad <= 0 ? 0f : Mathf.Clamp((float)LoadedCount / TotalToLoad, 0f, 1f);
        }

        public static LoadingSnapshot From(AssetLoadingSession session)
        {
            int loaded = ReadIntField(session, "_totalLoaded");
            int total = ReadIntField(session, "_totalToLoad", -1);
            if (total < 0)
            {
                total = loaded
                    + ReadCollectionCount(session, "_toLoad")
                    + ReadCollectionCount(session, "_loading")
                    + ReadCollectionCount(session, "_finalizing")
                    + ReadCollectionCount(session, "_vfxScenes")
                    + (ReadBoolField(session, "_vfxLoading") ? 1 : 0);
            }
            return new LoadingSnapshot(loaded, total);
        }

        private static int ReadIntField(object target, string fieldName, int fallback = 0)
        {
            try
            {
                FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object value = field?.GetValue(target);
                return value is IConvertible convertible ? Convert.ToInt32(convertible) : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool ReadBoolField(object target, string fieldName)
        {
            try
            {
                FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object value = field?.GetValue(target);
                return value is bool boolean && boolean;
            }
            catch
            {
                return false;
            }
        }

        private static int ReadCollectionCount(object target, string fieldName)
        {
            try
            {
                FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object value = field?.GetValue(target);
                return value is ICollection collection ? collection.Count : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
