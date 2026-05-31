using System;
using System.IO;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Redirects the game's normal save store to the currently selected Android
/// launch profile account root.  Java-side settings import/export and the C#
/// SaveManager must agree on the same directory for save/profile/settings
/// isolation to work.
/// </summary>
public static class SavePathPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(UserDataPathProvider), "GetAccountScopedBasePath", prefix: PatchHelper.Method(typeof(SavePathPatches), nameof(GetAccountScopedBasePathPrefix)));
        PatchHelper.Patch(harmony, typeof(UserDataPathProvider), "GetProfileScopedBasePath", prefix: PatchHelper.Method(typeof(SavePathPatches), nameof(GetProfileScopedBasePathPrefix)));
        PatchHelper.Patch(harmony, typeof(UserDataPathProvider), "GetProfileScopedPath", prefix: PatchHelper.Method(typeof(SavePathPatches), nameof(GetProfileScopedPathPrefix)));
        PatchHelper.Log($"Save path patches enabled. AccountRoot={AppPaths.AccountRoot}; ModsDir={AppPaths.ModsDir}; GameDir={AppPaths.GameDir}");
    }

    public static bool GetAccountScopedBasePathPrefix(string dataType, ref string __result)
    {
        __result = CombineGodotPath(ToGodotUserPath(AppPaths.AccountRoot), dataType);
        return false;
    }

    public static bool GetProfileScopedBasePathPrefix(int profileId, ref string __result)
    {
        __result = CombineGodotPath(ToGodotUserPath(AppPaths.AccountRoot), UserDataPathProvider.GetProfileDir(profileId));
        return false;
    }

    public static bool GetProfileScopedPathPrefix(int profileId, string dataType, ref string __result)
    {
        __result = CombineGodotPath(ToGodotUserPath(AppPaths.AccountRoot), UserDataPathProvider.GetProfileDir(profileId), dataType);
        return false;
    }

    private static string ToGodotUserPath(string absolutePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(absolutePath))
                Directory.CreateDirectory(absolutePath);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to ensure Android account root '{absolutePath}': {exception.Message}");
        }

        try
        {
            var dataDir = NormalizeAbsolute(AppPaths.DataDir);
            var fullPath = NormalizeAbsolute(absolutePath);
            if (string.IsNullOrWhiteSpace(dataDir) || string.IsNullOrWhiteSpace(fullPath))
                return "user://default/1";

            if (string.Equals(fullPath, dataDir, StringComparison.Ordinal))
                return "user://";

            var prefix = dataDir.EndsWith(Path.DirectorySeparatorChar) ? dataDir : dataDir + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                var relative = fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                return string.IsNullOrWhiteSpace(relative) ? "user://" : "user://" + relative.Trim('/');
            }

            PatchHelper.Log($"Account root is outside the Godot user data directory; falling back to user://default/1. dataDir={dataDir}; accountRoot={fullPath}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to localize Android account root '{absolutePath}': {exception.Message}");
        }

        return "user://default/1";
    }

    private static string NormalizeAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string CombineGodotPath(params string[] parts)
    {
        var result = string.Empty;
        foreach (var rawPart in parts)
        {
            var part = (rawPart ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(part))
                continue;

            if (string.IsNullOrEmpty(result))
            {
                result = part.EndsWith("://", StringComparison.Ordinal) ? part : part.TrimEnd('/');
                continue;
            }

            part = part.Trim('/');
            if (string.IsNullOrWhiteSpace(part))
                continue;
            result = result.EndsWith("://", StringComparison.Ordinal)
                ? result + part
                : result.TrimEnd('/') + "/" + part;
        }
        return string.IsNullOrWhiteSpace(result) ? "user://default/1" : result;
    }
}
