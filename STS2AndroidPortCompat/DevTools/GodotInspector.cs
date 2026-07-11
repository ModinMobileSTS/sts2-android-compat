using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using STS2Mobile.Android;

namespace STS2Mobile.DevTools;

public static class GodotInspector
{
    private const int MaxTreeNodes = 220;
    private const int MaxProperties = 260;
    private const int MaxPreviewLength = 180;
    private const string AuditLogRelative = "logs/dev-tools.log";

    public static JsonObject ListTree()
    {
        var tree = GetTree();
        if (tree?.Root == null)
            return Fail("SceneTree root is not ready.");

        var items = new JsonArray();
        var count = 0;
        AddNodeRecursive(items, tree.Root, "", 0, ref count);
        return new JsonObject
        {
            ["items"] = items,
            ["kind"] = "godot_tree",
            ["type"] = tree.Root.GetType().FullName,
        };
    }

    public static JsonObject InspectNode(string nodePath)
    {
        var node = ResolveNode(nodePath, out var error);
        if (node == null)
            return new JsonObject { ["error"] = error ?? "Node not found", ["items"] = new JsonArray() };

        var items = new JsonArray();
        var resolvedPath = SafePath(node);
        AddInfo(items, "Name", node.Name.ToString(), "StringName");
        AddInfo(items, "Path", resolvedPath, "NodePath");
        AddInfo(items, "Class", ClassName(node), node.GetType().FullName);
        AddInfo(items, "Children", node.GetChildCount().ToString(), "int");
        AddInfo(items, "ProcessMode", node.ProcessMode.ToString(), node.ProcessMode.GetType().Name);
        AddProperties(items, node, resolvedPath, "");

        return new JsonObject
        {
            ["items"] = items,
            ["kind"] = "godot_node",
            ["path"] = resolvedPath,
            ["type"] = ClassName(node),
        };
    }

    public static JsonObject InspectObject(string objectRef)
    {
        var target = ResolveObject(objectRef, out var error);
        if (target == null)
            return new JsonObject { ["error"] = error ?? "Object not found", ["items"] = new JsonArray() };

        var items = new JsonArray();
        var resolvedRef = ObjectRef(target);
        AddInfo(items, "Class", ClassName(target), target.GetType().FullName);
        AddInfo(items, "InstanceId", resolvedRef, "ulong");
        AddProperties(items, target, "", resolvedRef);
        return new JsonObject
        {
            ["items"] = items,
            ["kind"] = "godot_object",
            ["objectRef"] = resolvedRef,
            ["preview"] = PreviewObject(target),
            ["type"] = ClassName(target),
        };
    }

    public static JsonObject SetProperty(string nodePath, string objectRef, string property, JsonNode valueNode)
    {
        try
        {
            var target = ResolveTarget(nodePath, objectRef, out var error);
            if (target == null)
                return Fail(error ?? "Godot object not found.");
            if (string.IsNullOrWhiteSpace(property))
                return Fail("Property is empty.");

            var current = target.Get(property);
            var editKind = EditKind(current);
            if (string.IsNullOrEmpty(editKind))
                return Fail("Property type is not editable: " + current.VariantType);

            SetVariantValue(target, property, editKind, valueNode);
            var updated = target.Get(property);
            Audit($"GODOT SET {ObjectLabel(target)}:{property} = {Preview(updated)}");
            return new JsonObject { ["ok"] = true, ["preview"] = Preview(updated), ["type"] = updated.VariantType.ToString() };
        }
        catch (Exception exception)
        {
            Audit($"GODOT SET failed {nodePath}{objectRef}:{property}: {exception.Message}");
            return Fail(exception.GetBaseException().Message);
        }
    }

    public static JsonObject RunGdScript(string source, string nodePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source))
                return Fail("Script is empty.");

            var tree = GetTree();
            if (tree?.Root == null)
                return Fail("SceneTree root is not ready.");
            var node = string.IsNullOrWhiteSpace(nodePath) ? tree.Root : ResolveNode(nodePath, out _) ?? tree.Root;

            var script = new GDScript
            {
                SourceCode = WrapUserScript(source),
            };
            var reloadError = script.Reload();
            if (reloadError != Error.Ok)
                return Fail("GDScript compile failed: " + reloadError);

            var instance = script.New().AsGodotObject();
            if (instance == null)
                return Fail("GDScript instance creation failed.");

            var result = instance.Call("run", tree.Root, tree, node);
            Audit($"GODOT SCRIPT node={SafePath(node)} -> {Preview(result)}");
            return new JsonObject { ["ok"] = true, ["preview"] = Preview(result), ["type"] = result.VariantType.ToString() };
        }
        catch (Exception exception)
        {
            Audit($"GODOT SCRIPT failed {nodePath}: {exception.Message}");
            return Fail(exception.GetBaseException().Message);
        }
    }

    private static SceneTree GetTree() => Engine.GetMainLoop() as SceneTree;

    private static void AddNodeRecursive(JsonArray items, Node node, string parentPath, int depth, ref int count)
    {
        if (node == null || count >= MaxTreeNodes)
            return;
        var path = SafePath(node);
        var childCount = node.GetChildCount();
        items.Add(new JsonObject
        {
            ["domain"] = "godot",
            ["kind"] = "node",
            ["name"] = node.Name.ToString(),
            ["path"] = path,
            ["type"] = ClassName(node),
            ["preview"] = childCount == 1 ? "1 child · " + path : childCount + " children · " + path,
            ["canNavigate"] = true,
            ["canEdit"] = false,
            ["parentPath"] = parentPath,
            ["hasChildren"] = childCount > 0,
            ["depth"] = depth,
        });
        count++;
        foreach (Node child in node.GetChildren())
        {
            if (count >= MaxTreeNodes)
                break;
            AddNodeRecursive(items, child, path, depth + 1, ref count);
        }
    }

    private static void AddProperties(JsonArray items, GodotObject target, string nodePath, string objectRef)
    {
        var count = 0;
        foreach (Godot.Collections.Dictionary property in target.GetPropertyList())
        {
            if (count >= MaxProperties)
                break;
            var name = DictString(property, "name");
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("_", StringComparison.Ordinal))
                continue;

            Variant value;
            string preview;
            try
            {
                value = target.Get(name);
                preview = Preview(value);
            }
            catch (Exception exception)
            {
                value = default;
                preview = "!" + exception.GetBaseException().Message;
            }

            var typeName = VariantTypeName(value, DictInt(property, "type", (int)value.VariantType));
            var editKind = EditKind(value);
            var item = new JsonObject
            {
                ["domain"] = "godot",
                ["kind"] = "property",
                ["name"] = name,
                ["property"] = name,
                ["path"] = !string.IsNullOrWhiteSpace(nodePath)
                    ? nodePath + ":" + name
                    : objectRef + ":" + name,
                ["type"] = typeName,
                ["preview"] = preview,
                ["canEdit"] = editKind.Length > 0,
                ["editKind"] = editKind,
                ["canNavigate"] = false,
            };
            if (!string.IsNullOrWhiteSpace(nodePath))
                item["nodePath"] = nodePath;
            if (!string.IsNullOrWhiteSpace(objectRef))
                item["objectRef"] = objectRef;

            if (value.VariantType == Variant.Type.Object)
            {
                try
                {
                    var nested = value.AsGodotObject();
                    if (nested != null && GodotObject.IsInstanceValid(nested))
                    {
                        item["type"] = ClassName(nested);
                        item["canNavigate"] = true;
                        item["targetObjectRef"] = ObjectRef(nested);
                        item["targetKind"] = nested is Node ? "node" : "object";
                        if (nested is Node childNode && childNode.IsInsideTree())
                            item["targetNodePath"] = SafePath(childNode);
                    }
                }
                catch
                {
                }
            }

            items.Add(item);
            count++;
        }
    }

    private static Node ResolveNode(string nodePath, out string error)
    {
        error = null;
        try
        {
            var tree = GetTree();
            if (tree?.Root == null)
            {
                error = "SceneTree root is not ready.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(nodePath) || nodePath == SafePath(tree.Root))
                return tree.Root;

            var direct = tree.Root.GetNodeOrNull(new NodePath(nodePath));
            if (direct != null)
                return direct;

            var rootPath = SafePath(tree.Root);
            if (nodePath.StartsWith(rootPath + "/", StringComparison.Ordinal))
            {
                var relative = nodePath.Substring(rootPath.Length + 1);
                var relativeNode = tree.Root.GetNodeOrNull(new NodePath(relative));
                if (relativeNode != null)
                    return relativeNode;
            }

            error = "Node not found: " + nodePath;
            return null;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return null;
        }
    }

    private static GodotObject ResolveTarget(string nodePath, string objectRef, out string error)
    {
        if (!string.IsNullOrWhiteSpace(objectRef))
            return ResolveObject(objectRef, out error);
        return ResolveNode(nodePath, out error);
    }

    private static GodotObject ResolveObject(string objectRef, out string error)
    {
        error = null;
        try
        {
            var normalized = objectRef?.StartsWith("godot:", StringComparison.Ordinal) == true
                ? objectRef.Substring("godot:".Length)
                : objectRef;
            if (!ulong.TryParse(normalized, out var instanceId))
            {
                error = "Invalid Godot object reference: " + objectRef;
                return null;
            }
            var target = GodotObject.InstanceFromId(instanceId);
            if (target == null || !GodotObject.IsInstanceValid(target))
            {
                error = "Godot object is no longer valid: " + objectRef;
                return null;
            }
            return target;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return null;
        }
    }

    private static void AddInfo(JsonArray items, string name, string preview, string type)
    {
        items.Add(new JsonObject
        {
            ["domain"] = "godot",
            ["kind"] = "info",
            ["name"] = name,
            ["type"] = type,
            ["preview"] = preview ?? "",
            ["canNavigate"] = false,
            ["canEdit"] = false,
        });
    }

    private static string DictString(Godot.Collections.Dictionary dictionary, string key)
    {
        try
        {
            return dictionary.ContainsKey(key) ? dictionary[key].AsString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int DictInt(Godot.Collections.Dictionary dictionary, string key, int fallback)
    {
        try
        {
            return dictionary.ContainsKey(key) ? dictionary[key].AsInt32() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ClassName(GodotObject value)
    {
        try
        {
            var godotClass = value.GetClass();
            var managed = value.GetType().Name;
            return string.Equals(godotClass, managed, StringComparison.Ordinal) ? managed : managed + " / " + godotClass;
        }
        catch
        {
            return value.GetType().FullName;
        }
    }

    private static string ObjectRef(GodotObject value) => "godot:" + value.GetInstanceId();

    private static string ObjectLabel(GodotObject value) => value is Node node ? SafePath(node) : ObjectRef(value);

    private static string SafePath(Node node)
    {
        try
        {
            return node.GetPath().ToString();
        }
        catch
        {
            return node?.Name.ToString() ?? "";
        }
    }

    private static string VariantTypeName(Variant value, int propertyType)
    {
        try
        {
            var type = value.VariantType == Variant.Type.Nil && propertyType > 0 ? (Variant.Type)propertyType : value.VariantType;
            return type.ToString();
        }
        catch
        {
            return value.VariantType.ToString();
        }
    }

    private static string EditKind(Variant value)
    {
        return value.VariantType switch
        {
            Variant.Type.Bool => "bool",
            Variant.Type.Int => "int",
            Variant.Type.Float => "float",
            Variant.Type.String => "string",
            Variant.Type.StringName => "string",
            Variant.Type.NodePath => "string",
            _ => string.Empty,
        };
    }

    private static void SetVariantValue(GodotObject target, string property, string editKind, JsonNode valueNode)
    {
        switch (editKind)
        {
            case "bool":
                target.Set(property, valueNode?.GetValue<bool>() ?? false);
                break;
            case "int":
                target.Set(property, valueNode?.GetValue<long>() ?? 0L);
                break;
            case "float":
                target.Set(property, valueNode?.GetValue<double>() ?? 0.0);
                break;
            default:
                target.Set(property, valueNode?.GetValue<string>() ?? string.Empty);
                break;
        }
    }

    private static string WrapUserScript(string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("extends RefCounted");
        sb.AppendLine("func run(root, tree, node):");
        var normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
            sb.AppendLine("\t" + line);
        return sb.ToString();
    }

    private static string Preview(Variant value)
    {
        try
        {
            return value.VariantType switch
            {
                Variant.Type.Nil => "null",
                Variant.Type.Bool => value.AsBool() ? "true" : "false",
                Variant.Type.Int => value.AsInt64().ToString(),
                Variant.Type.Float => value.AsDouble().ToString("G"),
                Variant.Type.String => Truncate(value.AsString()),
                Variant.Type.StringName => Truncate(value.AsStringName().ToString()),
                Variant.Type.NodePath => Truncate(value.AsNodePath().ToString()),
                Variant.Type.Object => PreviewObject(value),
                _ => Truncate(value.ToString()),
            };
        }
        catch (Exception exception)
        {
            return "!" + exception.GetBaseException().Message;
        }
    }

    private static string PreviewObject(Variant value)
    {
        var obj = value.AsGodotObject();
        return PreviewObject(obj);
    }

    private static string PreviewObject(GodotObject obj)
    {
        if (obj == null)
            return "null";
        if (obj is Node node)
            return Truncate(node.ToString() + " · " + SafePath(node));
        try
        {
            var rendered = obj.ToString();
            if (!string.IsNullOrWhiteSpace(rendered) && rendered != obj.GetType().FullName)
                return Truncate(rendered);
        }
        catch
        {
        }
        return ClassName(obj) + " · " + ObjectRef(obj);
    }

    private static JsonObject Fail(string message) => new JsonObject { ["ok"] = false, ["error"] = message };

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
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
}
