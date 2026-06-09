using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace STS2Mobile.Patches;

public static class MapDrawingSceneCachePatches
{
    private const string LineDrawScenePath = "res://scenes/screens/map/map_line_draw.tscn";
    private const string LineEraseScenePath = "res://scenes/screens/map/map_line_erase.tscn";

    private static readonly ConditionalWeakTable<NMapDrawings, SceneCache> SceneCaches = new();
    private static readonly FieldInfo EraserMaterialField = AccessTools.Field(typeof(NMapDrawings), "_eraserMaterial");
    private static bool _loggedFallbackEnabled;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NMapDrawings), "CreateLineForPlayer", prefix: PatchHelper.Method(typeof(MapDrawingSceneCachePatches), nameof(CreateLineForPlayerPrefix)));
        PatchHelper.Log("Android map drawing scene cache safety patch enabled.");
    }

    public static bool CreateLineForPlayerPrefix(NMapDrawings __instance, Player player, bool isErasing, ref Line2D __result)
    {
        try
        {
            if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
                return true;

            var cache = SceneCaches.GetOrCreateValue(__instance);
            Line2D line = cache.CreateLine(isErasing);
            if (line == null)
                return true;

            line.DefaultColor = player.Character.MapDrawingColor;
            line.ClearPoints();
            line.Position = Vector2.Zero;
            if (isErasing)
                TrySetEraserMaterial(__instance, line.Material);

            __result = line;
            if (!_loggedFallbackEnabled)
            {
                _loggedFallbackEnabled = true;
                PatchHelper.Log("Map drawing lines now instantiate from Android-owned PackedScene resources to avoid disposed PreloadManager cache entries.");
            }
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Map drawing scene cache safety failed; using original path: {exception.Message}");
            return true;
        }
    }

    private static void TrySetEraserMaterial(NMapDrawings drawings, Material material)
    {
        try
        {
            if (EraserMaterialField != null && material != null && GodotObject.IsInstanceValid(material))
                EraserMaterialField.SetValue(drawings, material);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Map drawing eraser material refresh failed: {exception.Message}");
        }
    }

    private sealed class SceneCache
    {
        private PackedScene _drawScene;
        private PackedScene _eraseScene;

        public Line2D CreateLine(bool isErasing)
        {
            PackedScene scene = GetScene(isErasing);
            return scene?.Instantiate<Line2D>(PackedScene.GenEditState.Disabled);
        }

        private PackedScene GetScene(bool isErasing)
        {
            PackedScene scene = isErasing ? _eraseScene : _drawScene;
            if (IsUsable(scene))
                return scene;

            string path = isErasing ? LineEraseScenePath : LineDrawScenePath;
            scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Ignore);
            if (!IsUsable(scene))
            {
                PatchHelper.Log($"Map drawing scene reload failed: {path}");
                return null;
            }

            if (isErasing)
                _eraseScene = scene;
            else
                _drawScene = scene;
            return scene;
        }

        private static bool IsUsable(PackedScene scene)
        {
            try
            {
                return scene != null && GodotObject.IsInstanceValid(scene);
            }
            catch
            {
                return false;
            }
        }
    }
}
