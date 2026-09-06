using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace STS2Mobile.Patches;

// One bounded snapshot, not an ever-growing directory of historical profiles.
internal static class LearnedWarmAssetStore
{
    internal static HashSet<string> Read(string path, string context, int limit, Func<string, bool> accept)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return paths;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out int version) || version != 2
            || !root.TryGetProperty("context", out var savedContext) || savedContext.ValueKind != JsonValueKind.String
            || !string.Equals(savedContext.GetString(), context, StringComparison.Ordinal)
            || !root.TryGetProperty("paths", out var values) || values.ValueKind != JsonValueKind.Array)
            return paths;
        foreach (var value in values.EnumerateArray())
        {
            if (paths.Count >= limit)
                break;
            if (value.ValueKind == JsonValueKind.String && accept(value.GetString()))
                paths.Add(value.GetString());
        }
        return paths;
    }

    internal static void Write(string path, string context, string[] paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { schema = 2, context, paths }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
