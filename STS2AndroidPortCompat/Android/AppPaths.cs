using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace STS2Mobile.Android;

public static class AppPaths
{
    private const string KnownPackageName = "com.megacrit.sts2re";

    private static string _dataDir;
    private static string _gameDir;
    private static string _accountRoot;
    private static string _modsDir;

    public static string DataDir => _dataDir ??= ResolveDataDir();
    public static string GameDir => _gameDir ??= ResolveLaunchContextPath("selected_game_dir", Path.Combine(DataDir, "game"));
    public static string ReleaseInfoPath => Path.Combine(GameDir, "release_info.json");
    public static string AccountRoot => _accountRoot ??= ResolveLaunchContextPath("selected_account_root", ResolveLegacyAccountRoot());
    public static string SettingsPath => Path.Combine(AccountRoot, "settings.save");
    public static string PendingUnlockAllPath => Path.Combine(AccountRoot, "pending_unlock_all.flag");
    public static string ModsDir => _modsDir ??= ResolveLaunchContextPath("selected_mods_dir", Path.Combine(DataDir, "mods"));

    private static string ResolveLaunchContextPath(string jsonKey, string fallback)
    {
        try
        {
            var contextPath = Path.Combine(DataDir, "launcher", "selected_instance.json");
            if (File.Exists(contextPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(contextPath));
                if (document.RootElement.TryGetProperty(jsonKey, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    var value = NormalizeAbsolutePath(element.GetString());
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            else
            {
                PatchHelper.Log($"Launch context not found while resolving {jsonKey}: {contextPath}");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Launch context file path lookup failed for {jsonKey}: {exception.Message}");
        }

        return NormalizeAbsolutePath(fallback);
    }

    private static string ResolveLegacyAccountRoot()
    {
        var defaultRoot = Path.Combine(DataDir, "default");
        try
        {
            if (Directory.Exists(defaultRoot))
            {
                var accountDirectories = Directory.GetDirectories(defaultRoot);
                if (accountDirectories.Length > 0)
                {
                    Array.Sort(accountDirectories, StringComparer.OrdinalIgnoreCase);
                    return NormalizeAbsolutePath(accountDirectories[0]);
                }
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to resolve legacy account data directory; falling back to default/1: {exception.Message}");
        }
        return Path.Combine(defaultRoot, "1");
    }

    private static string ResolveDataDir()
    {
        foreach (var candidate in GetDataDirCandidates())
        {
            if (TryAcceptFilesDirCandidate(candidate, out var normalized))
            {
                PatchHelper.Log($"Android data dir resolved for launch profile paths: {normalized}");
                return normalized;
            }
        }

        var hardFallback = NormalizeAbsolutePath($"/data/data/{KnownPackageName}/files");
        PatchHelper.Log($"Failed to resolve Android data dir from assembly/process candidates; falling back to {hardFallback}");
        return hardFallback;
    }

    private static IEnumerable<string> GetDataDirCandidates()
    {
        yield return TryResolveFilesDirFromPublishDir(Path.GetDirectoryName(typeof(AppPaths).Assembly.Location));
        yield return TryResolveFilesDirFromPublishDir(AppContext.BaseDirectory);
        yield return TryResolveFilesDirFromPublishDir(Directory.GetCurrentDirectory());

        foreach (var candidate in TryResolveFilesDirsFromProcessName())
            yield return candidate;

        yield return $"/data/user/0/{KnownPackageName}/files";
        yield return $"/data/data/{KnownPackageName}/files";

        yield return TryBuildEnvironmentCandidate("HOME", null);
        yield return Directory.GetCurrentDirectory();
    }

    private static bool TryAcceptFilesDirCandidate(string candidate, out string normalized)
    {
        normalized = NormalizeAbsolutePath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) || normalized == Path.DirectorySeparatorChar.ToString())
            return false;

        try
        {
            // Strong signal: Java wrote the launch context here before Godot was started.
            if (File.Exists(Path.Combine(normalized, "launcher", "selected_instance.json")))
                return true;

            // Normal Android app-private files directory.  Accept it even before
            // selected_instance.json exists so fallback profile-less startup still
            // resolves to app-private paths rather than to /default or /mods.
            var slashPath = normalized.Replace('\\', '/');
            if ((slashPath.StartsWith("/data/user/", StringComparison.Ordinal) || slashPath.StartsWith("/data/data/", StringComparison.Ordinal))
                && slashPath.EndsWith("/files", StringComparison.Ordinal)
                && Directory.Exists(normalized))
            {
                return true;
            }

            // Publish-dir-derived candidates should have the Godot mono tree below them.
            if (Directory.Exists(Path.Combine(normalized, ".godot", "mono")))
                return true;
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<string> TryResolveFilesDirsFromProcessName()
    {
        string packageName = null;
        try
        {
            var bytes = File.ReadAllBytes("/proc/self/cmdline");
            var raw = Encoding.UTF8.GetString(bytes);
            var processName = raw.Split('\0')[0].Trim();
            if (!string.IsNullOrWhiteSpace(processName))
            {
                var colon = processName.IndexOf(':');
                packageName = colon > 0 ? processName.Substring(0, colon) : processName;
            }
        }
        catch
        {
            // Use the known package fallback below.
        }

        if (string.IsNullOrWhiteSpace(packageName) || !packageName.Contains('.'))
            packageName = KnownPackageName;

        yield return $"/data/user/0/{packageName}/files";
        yield return $"/data/data/{packageName}/files";
    }

    private static string TryResolveFilesDirFromPublishDir(string assemblyDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assemblyDir))
                return null;

            var dir = new DirectoryInfo(assemblyDir);
            // Expected: <files>/.godot/mono/publish/arm64/STS2Mobile.dll
            if (dir.Name == "arm64"
                && dir.Parent?.Name == "publish"
                && dir.Parent.Parent?.Name == "mono"
                && dir.Parent.Parent.Parent?.Name == ".godot"
                && dir.Parent.Parent.Parent.Parent != null)
            {
                return dir.Parent.Parent.Parent.Parent.FullName;
            }
        }
        catch
        {
            // Try the next candidate.
        }
        return null;
    }

    private static string TryBuildEnvironmentCandidate(string variable, string relativePath)
    {
        try
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root))
                return null;
            return string.IsNullOrWhiteSpace(relativePath) ? root : Path.Combine(root, relativePath);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
