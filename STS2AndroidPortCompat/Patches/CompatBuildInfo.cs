using System;
using System.Reflection;

namespace STS2Mobile.Patches;

public static class CompatBuildInfo
{
    public static void Log()
    {
        try
        {
            var assembly = typeof(CompatBuildInfo).Assembly;
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
            string Get(string key)
            {
                foreach (var item in metadata)
                {
                    if (string.Equals(item.Key, key, StringComparison.Ordinal))
                        return item.Value ?? string.Empty;
                }
                return string.Empty;
            }

            var branch = Get("GitBranch");
            var commit = Get("GitCommit");
            var subject = Get("GitCommitSubject");
            var dirty = Get("GitDirty");
            var builtAt = Get("BuildTimestampUtc");
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? string.Empty;
            PatchHelper.Log($"Compat build info: version={version}; branch={branch}; commit={commit}; dirty={dirty}; built_utc={builtAt}; subject={subject}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Compat build info unavailable: {exception.Message}");
        }
    }
}
