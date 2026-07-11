using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Android;

namespace STS2Mobile.DevTools;

public static class ReflectionInspector
{
    private const int MaxMembers = 250;
    private const int MaxPreviewLength = 160;
    private const string AuditLogRelative = "logs/dev-tools.log";

    public static JsonObject ListRoots()
    {
        var items = new JsonArray();
        AddRoot(items, "save_manager", "SaveManager.Instance", SafeGet(() => (object)SaveManager.Instance));
        AddRoot(items, "settings_save", "SettingsSave", SafeGet(() => (object)SaveManager.Instance?.SettingsSave));
        AddRoot(items, "ngame", "NGame.Instance", SafeGet(() => (object)NGame.Instance));
        AddRoot(items, "run_manager", "RunManager.Instance", SafeGet(() => (object)RunManager.Instance));
        AddRoot(items, "run_state", "RunManager.Instance.DebugState/Current", SafeGet(GetRunState));
        AddRoot(items, "app_paths", "AppPaths", new AppPathsSnapshot());
        AddRoot(items, "companion_settings", "Companion settings.save", new CompanionSettingsSnapshot());
        return new JsonObject { ["items"] = items };
    }

    public static JsonObject ListMembers(string path)
    {
        var target = ResolvePath(path, out var error);
        if (target == null)
            return new JsonObject { ["error"] = error ?? "Not found", ["items"] = new JsonArray() };

        var items = new JsonArray();
        if (target is IDictionary dictionary)
        {
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxMembers)
                    break;
                var key = entry.Key?.ToString() ?? "null";
                items.Add(MemberItem(key, "entry", entry.Value, path + "/" + EscapeSegment(key)));
            }
            return new JsonObject { ["items"] = items, ["kind"] = "dictionary" };
        }

        if (target is IEnumerable enumerable && target is not string)
        {
            var index = 0;
            foreach (var entry in enumerable)
            {
                if (index >= MaxMembers)
                    break;
                items.Add(MemberItem("[" + index + "]", "element", entry, path + "/[" + index + "]"));
                index++;
            }
            return new JsonObject { ["items"] = items, ["kind"] = "collection", ["count"] = index };
        }

        var type = target.GetType();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (items.Count >= MaxMembers)
                break;
            if (prop.GetIndexParameters().Length > 0 || !seen.Add(prop.Name))
                continue;
            object value = null;
            string readError = null;
            try
            {
                if (prop.CanRead)
                    value = prop.GetValue(target);
                else
                    readError = "not readable";
            }
            catch (Exception exception)
            {
                readError = exception.GetBaseException().Message;
            }
            items.Add(MemberItem(prop.Name, "property", value, path + "/" + prop.Name, prop.CanWrite && IsEditableType(prop.PropertyType), EditKind(prop.PropertyType), prop.PropertyType, readError));
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (items.Count >= MaxMembers)
                break;
            if (!seen.Add(field.Name))
                continue;
            object value = null;
            string readError = null;
            try
            {
                value = field.GetValue(target);
            }
            catch (Exception exception)
            {
                readError = exception.GetBaseException().Message;
            }
            items.Add(MemberItem(field.Name, "field", value, path + "/" + field.Name, !field.IsInitOnly && IsEditableType(field.FieldType), EditKind(field.FieldType), field.FieldType, readError));
        }
        return new JsonObject { ["items"] = items, ["type"] = type.FullName };
    }

    public static JsonObject SetValue(string path, JsonNode valueNode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !path.Contains('/'))
                return Fail("Path must include a parent and member.");

            var lastSlash = path.LastIndexOf('/');
            var parentPath = path.Substring(0, lastSlash);
            var member = UnescapeSegment(path.Substring(lastSlash + 1));
            if (member.StartsWith("[", StringComparison.Ordinal))
                return Fail("Collection element assignment is not supported.");

            var parent = ResolvePath(parentPath, out var error);
            if (parent == null)
                return Fail(error ?? "Parent not found.");

            var type = parent.GetType();
            var prop = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                if (!prop.CanWrite)
                    return Fail("Property is not writable.");
                var converted = ConvertNode(valueNode, prop.PropertyType);
                prop.SetValue(parent, converted);
                Audit($"SET prop {path} = {Preview(converted)}");
                return new JsonObject { ["ok"] = true, ["preview"] = Preview(converted) };
            }
            var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                if (field.IsInitOnly)
                    return Fail("Field is init-only.");
                var converted = ConvertNode(valueNode, field.FieldType);
                field.SetValue(parent, converted);
                Audit($"SET field {path} = {Preview(converted)}");
                return new JsonObject { ["ok"] = true, ["preview"] = Preview(converted) };
            }
            return Fail("Member not found: " + member);
        }
        catch (Exception exception)
        {
            Audit($"SET failed {path}: {exception.Message}");
            return Fail(exception.GetBaseException().Message);
        }
    }

    private static JsonObject Fail(string message) => new JsonObject { ["ok"] = false, ["error"] = message };

    private static void AddRoot(JsonArray items, string id, string name, object value)
    {
        items.Add(new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["path"] = id,
            ["type"] = value?.GetType().FullName ?? "null",
            ["preview"] = Preview(value),
            ["canNavigate"] = value != null,
            ["canEdit"] = false,
        });
    }

    private static JsonObject MemberItem(string name, string kind, object value, string path, bool canEdit = false, string editKind = "", Type valueType = null, string readError = null)
    {
        var navigable = value != null && !IsEditableType(value.GetType()) && value is not string;
        return new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["path"] = path,
            ["type"] = valueType?.FullName ?? value?.GetType().FullName ?? "null",
            ["preview"] = readError != null ? ("!" + readError) : Preview(value),
            ["canNavigate"] = navigable,
            ["canEdit"] = canEdit,
            ["editKind"] = editKind ?? "",
        };
    }

    private static object ResolvePath(string path, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Empty path";
            return null;
        }
        var segments = SplitPath(path);
        if (segments.Count == 0)
        {
            error = "Empty path";
            return null;
        }

        object current = ResolveRoot(segments[0], out error);
        if (current == null)
            return null;

        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.StartsWith("[", StringComparison.Ordinal) && segment.EndsWith("]", StringComparison.Ordinal)
                && int.TryParse(segment.Substring(1, segment.Length - 2), out var index))
            {
                if (current is IList list)
                {
                    if (index < 0 || index >= list.Count)
                    {
                        error = "Index out of range";
                        return null;
                    }
                    current = list[index];
                    continue;
                }
                if (current is IEnumerable enumerable && current is not string)
                {
                    var n = 0;
                    object found = null;
                    var hit = false;
                    foreach (var item in enumerable)
                    {
                        if (n == index)
                        {
                            found = item;
                            hit = true;
                            break;
                        }
                        n++;
                    }
                    if (!hit)
                    {
                        error = "Index out of range";
                        return null;
                    }
                    current = found;
                    continue;
                }
                error = "Not indexable";
                return null;
            }

            if (current is IDictionary dictionary)
            {
                if (dictionary.Contains(segment))
                {
                    current = dictionary[segment];
                    continue;
                }
                // try string keys loosely
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key?.ToString(), segment, StringComparison.Ordinal))
                    {
                        current = entry.Value;
                        goto next;
                    }
                }
                error = "Key not found: " + segment;
                return null;
            }

            var type = current.GetType();
            var prop = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.GetIndexParameters().Length == 0 && prop.CanRead)
            {
                current = prop.GetValue(current);
                continue;
            }
            var field = type.GetField(segment, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                current = field.GetValue(current);
                continue;
            }
            error = "Member not found: " + segment;
            return null;
            next: ;
        }
        return current;
    }

    private static object ResolveRoot(string id, out string error)
    {
        error = null;
        try
        {
            return id switch
            {
                "save_manager" => SaveManager.Instance,
                "settings_save" => SaveManager.Instance?.SettingsSave,
                "ngame" => NGame.Instance,
                "run_manager" => RunManager.Instance,
                "run_state" => GetRunState(),
                "app_paths" => new AppPathsSnapshot(),
                "companion_settings" => new CompanionSettingsSnapshot(),
                _ => null,
            } ?? FailRoot(id, out error);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private static object FailRoot(string id, out string error)
    {
        error = "Root unavailable: " + id;
        return null;
    }

    private static object GetRunState()
    {
        try
        {
            var instance = RunManager.Instance;
            if (instance == null)
                return null;
            foreach (var name in new[] { "DebugState", "State", "CurrentRunState", "RunState" })
            {
                var prop = typeof(RunManager).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop?.CanRead == true)
                {
                    var value = prop.GetValue(instance);
                    if (value != null)
                        return value;
                }
                var field = typeof(RunManager).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    var value = field.GetValue(instance);
                    if (value != null)
                        return value;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static object SafeGet(Func<object> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SplitPath(string path)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '/')
            {
                if (sb.Length > 0)
                {
                    parts.Add(UnescapeSegment(sb.ToString()));
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
            parts.Add(UnescapeSegment(sb.ToString()));
        return parts;
    }

    private static string EscapeSegment(string value) => (value ?? "").Replace("/", "%2F");
    private static string UnescapeSegment(string value) => (value ?? "").Replace("%2F", "/");

    private static bool IsEditableType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(string)
            || type.IsEnum;
    }

    private static string EditKind(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return "bool";
        if (type == typeof(int) || type == typeof(long)) return "int";
        if (type == typeof(float) || type == typeof(double)) return "float";
        if (type == typeof(string)) return "string";
        if (type.IsEnum) return "enum";
        return "";
    }

    private static object ConvertNode(JsonNode node, Type targetType)
    {
        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (node == null || node is JsonValue value && value.TryGetValue<string>(out var s) && s == null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                return Activator.CreateInstance(targetType);
            return null;
        }
        if (targetType == typeof(string))
            return node.ToString().Trim('"');
        if (targetType == typeof(bool))
            return node.GetValue<bool>();
        if (targetType == typeof(int))
            return node.GetValue<int>();
        if (targetType == typeof(long))
            return node.GetValue<long>();
        if (targetType == typeof(float))
            return node.GetValue<float>();
        if (targetType == typeof(double))
            return node.GetValue<double>();
        if (targetType.IsEnum)
        {
            var text = node.GetValue<string>() ?? node.ToString();
            return Enum.Parse(targetType, text, true);
        }
        throw new InvalidOperationException("Unsupported type: " + targetType.Name);
    }

    private static string Preview(object value)
    {
        if (value == null)
            return "null";
        try
        {
            if (value is string text)
                return Truncate(text);
            if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum)
                return value.ToString();
            if (value is ICollection collection)
                return value.GetType().Name + "[" + collection.Count + "]";
            var rendered = value.ToString();
            if (string.IsNullOrEmpty(rendered) || rendered == value.GetType().FullName)
                return value.GetType().Name;
            return Truncate(rendered);
        }
        catch (Exception exception)
        {
            return "!" + exception.Message;
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length <= MaxPreviewLength ? value : value.Substring(0, MaxPreviewLength) + "…";
    }

    private static void Audit(string message)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, AuditLogRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppPaths.DataDir);
            File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + message + "\n");
        }
        catch
        {
        }
    }

    private sealed class AppPathsSnapshot
    {
        public string DataDir => AppPaths.DataDir;
        public string GameDir => AppPaths.GameDir;
        public string AccountRoot => AppPaths.AccountRoot;
        public string SettingsPath => AppPaths.SettingsPath;
        public string ModsDir => AppPaths.ModsDir;
    }

    private sealed class CompanionSettingsSnapshot
    {
        public string Raw
        {
            get
            {
                AndroidSettingsBridge.TryReadRaw(out var json);
                return json ?? "";
            }
        }

        public bool OverlayEnabled => AndroidSettingsBridge.GetBool("android_in_game_overlay_enabled", false);
        public bool DevToolsEnabled => AndroidSettingsBridge.GetBool("android_dev_tools_enabled", false);
        public bool InspectorWritable => AndroidSettingsBridge.GetBool("android_dev_inspector_writable", false);
        public string TooltipMode => AndroidSettingsBridge.GetString("mobile_tooltip_mode", "immediate");
        public bool PreloadEnabled => AndroidSettingsBridge.GetBool("preload_enabled", true);
        public string ScreenRotation => AndroidSettingsBridge.GetString("android_screen_rotation_mode", "user_landscape");
    }
}
