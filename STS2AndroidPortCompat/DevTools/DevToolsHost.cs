using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using STS2Mobile.Android;
using STS2Mobile.Patches;

namespace STS2Mobile.DevTools;

/// <summary>
/// File-based Java ↔ C# bridge for the in-game overlay.
/// Polls launcher/devtools/request.json and writes the request-specific response file.
/// </summary>
public static class DevToolsHost
{
    private const string NodeName = "STS2MobileDevToolsHost";
    private const string LegacyResponseFileName = "response.json";
    private const string HostMarkerFileName = "host.json";
    private const int ProtocolVersion = 2;
    private const double RequestPollIntervalSeconds = 0.12;
    private static bool _started;
    private static bool _pollLoopStarted;
    private static bool _pollTickScheduled;
    private static long _lastPollUnixMs;
    private static Node _node;

    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            Callable.From(InstallWhenReady).CallDeferred();
        }
        catch (Exception exception)
        {
            _started = false;
            PatchHelper.Log($"[DevTools] schedule failed: {exception.Message}");
        }
    }

    private static void InstallWhenReady()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            try
            {
                Callable.From(InstallWhenReady).CallDeferred();
            }
            catch (Exception exception)
            {
                _started = false;
                PatchHelper.Log($"[DevTools] ready retry schedule failed: {exception.Message}");
            }
            return;
        }
        try
        {
            var existing = tree.Root.GetNodeOrNull(NodeName);
            if (existing != null)
            {
                _node = existing;
                _node.ProcessMode = Node.ProcessModeEnum.Always;
                _node.SetProcess(true);
                WriteReadyMarker();
                StartPollLoop();
                return;
            }

            _node = new DevToolsProcessNode { Name = NodeName, ProcessMode = Node.ProcessModeEnum.Always };
            tree.Root.AddChild(_node);
            _node.SetProcess(true);
            WriteReadyMarker();
            StartPollLoop();
            PatchHelper.Log($"[DevTools] host installed (protocol={ProtocolVersion}, dir={DevToolsDir}).");
        }
        catch (Exception exception)
        {
            _node = null;
            _started = false;
            PatchHelper.Log($"[DevTools] host install failed: {exception}");
        }
    }

    private static void StartPollLoop()
    {
        if (_pollLoopStarted)
            return;
        _pollLoopStarted = true;
        SchedulePollTick();
    }

    private static void SchedulePollTick()
    {
        if (!_pollLoopStarted || _pollTickScheduled)
            return;
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                Callable.From(SchedulePollTick).CallDeferred();
                return;
            }
            _pollTickScheduled = true;
            var timer = tree.CreateTimer(RequestPollIntervalSeconds);
            timer.Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(PollTick));
        }
        catch (Exception exception)
        {
            _pollTickScheduled = false;
            _pollLoopStarted = false;
            PatchHelper.Log($"[DevTools] poll schedule failed: {exception.Message}");
        }
    }

    private static void PollTick()
    {
        _pollTickScheduled = false;
        if (!_pollLoopStarted)
            return;

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastPollUnixMs >= (long)(RequestPollIntervalSeconds * 1000))
            {
                _lastPollUnixMs = now;
                TryProcessRequest();
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] poll tick failed: {exception.Message}");
        }
        finally
        {
            SchedulePollTick();
        }
    }

    private sealed class DevToolsProcessNode : Node
    {
        private double _elapsed;

        public override void _Process(double delta)
        {
            _elapsed += delta;
            if (_elapsed < RequestPollIntervalSeconds)
                return;
            _elapsed = 0;
            TryProcessRequest();
        }
    }

    // Java waits under Context.getFilesDir()/launcher/devtools.  Godot's
    // OS.GetDataDir can point at the engine userdata directory on Android, so
    // prefer AppPaths' files-dir resolver and only accept OS.GetDataDir when it
    // clearly resolves to the same app-private files root.
    private static string DevToolsDir => Path.Combine(ResolveDataDir(), "launcher", "devtools");
    private static string RequestPath => Path.Combine(DevToolsDir, "request.json");
    private static string ResponsePath => Path.Combine(DevToolsDir, LegacyResponseFileName);
    private static string HostMarkerPath => Path.Combine(DevToolsDir, HostMarkerFileName);

    private static string ResolveDataDir()
    {
        var appPathsDataDir = SafeResolveAppPathsDataDir();
        if (TryAcceptJavaFilesDir(appPathsDataDir, out var appPathsAccepted))
            return appPathsAccepted;

        try
        {
            var dataDir = OS.GetDataDir();
            if (TryAcceptJavaFilesDir(dataDir, out var godotAccepted))
                return godotAccepted;
            if (!string.IsNullOrWhiteSpace(dataDir))
                PatchHelper.Log($"[DevTools] Ignoring non-files OS.GetDataDir for Java bridge: {dataDir}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] OS.GetDataDir lookup failed: {exception.Message}");
        }

        if (!string.IsNullOrWhiteSpace(appPathsDataDir))
            return appPathsDataDir;
        return AppPaths.DataDir;
    }

    private static string SafeResolveAppPathsDataDir()
    {
        try
        {
            return AppPaths.DataDir;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] AppPaths.DataDir lookup failed: {exception.Message}");
            return string.Empty;
        }
    }

    private static bool TryAcceptJavaFilesDir(string candidate, out string accepted)
    {
        accepted = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        try
        {
            var normalized = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
                return false;

            if (File.Exists(Path.Combine(normalized, "launcher", "selected_instance.json")))
            {
                accepted = normalized;
                return true;
            }

            var slashPath = normalized.Replace('\\', '/');
            if ((slashPath.StartsWith("/data/user/", StringComparison.Ordinal) || slashPath.StartsWith("/data/data/", StringComparison.Ordinal))
                && slashPath.EndsWith("/files", StringComparison.Ordinal))
            {
                accepted = normalized;
                return true;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] files-dir candidate rejected ({candidate}): {exception.Message}");
        }
        return false;
    }

    private static void TryProcessRequest()
    {
        try
        {
            if (!File.Exists(RequestPath))
                return;
            string raw;
            try
            {
                raw = File.ReadAllText(RequestPath);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(raw))
                return;

            // Claim request by renaming; avoids double-processing.
            var processingPath = RequestPath + ".processing";
            try
            {
                if (File.Exists(processingPath))
                    File.Delete(processingPath);
                File.Move(RequestPath, processingPath);
            }
            catch
            {
                return;
            }

            JsonObject response;
            var responsePath = ResponsePath;
            JsonObject request = null;
            try
            {
            request = JsonNode.Parse(File.ReadAllText(processingPath))?.AsObject() ?? new JsonObject();
            responsePath = ResolveResponsePath(request);
            PatchHelper.Log($"[DevTools] processing request op={request["op"]?.GetValue<string>() ?? ""} id={TryGetRequestId(request)} response={Path.GetFileName(responsePath)}.");
            response = HandleRequest(request);
            }
            catch (Exception exception)
            {
                response = new JsonObject
                {
                    ["id"] = TryGetRequestId(request),
                    ["ok"] = false,
                    ["error"] = exception.Message,
                };
            }
            finally
            {
                try
                {
                    if (File.Exists(processingPath))
                        File.Delete(processingPath);
                }
                catch
                {
                }
            }

            Directory.CreateDirectory(DevToolsDir);
            WriteTextAtomically(responsePath, response.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] process failed: {exception.Message}");
        }
    }

    private static string TryGetRequestId(JsonObject request)
    {
        try
        {
            return request?["id"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveResponsePath(JsonObject request)
    {
        var fileName = LegacyResponseFileName;
        try
        {
            if (request.TryGetPropertyValue("response_file", out var node) && node != null)
                fileName = node.GetValue<string>();
        }
        catch
        {
            fileName = LegacyResponseFileName;
        }
        if (!IsSafeResponseFileName(fileName))
            fileName = LegacyResponseFileName;
        return Path.Combine(DevToolsDir, fileName);
    }

    private static bool IsSafeResponseFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 128 || !fileName.EndsWith(".json", StringComparison.Ordinal))
            return false;
        if (!fileName.StartsWith("response-", StringComparison.Ordinal) && !string.Equals(fileName, LegacyResponseFileName, StringComparison.Ordinal))
            return false;
        foreach (var character in fileName)
        {
            if (!char.IsLetterOrDigit(character) && character != '-' && character != '_' && character != '.')
                return false;
        }
        return true;
    }

    private static void WriteReadyMarker()
    {
        try
        {
            var marker = new JsonObject
            {
                ["ready"] = true,
                ["protocol"] = ProtocolVersion,
                ["updated_at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            WriteTextAtomically(HostMarkerPath, marker.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] host marker write failed: {exception.Message}");
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporaryPath, path);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The next host heartbeat/request can clean up a failed temporary file.
            }
        }
    }

    private static JsonObject HandleRequest(JsonObject request)
    {
        var id = request["id"]?.GetValue<string>() ?? "";
        var op = request["op"]?.GetValue<string>() ?? "";
        var response = new JsonObject { ["id"] = id, ["ok"] = true };

        switch (op)
        {
            case "ping":
                response["payload"] = new JsonObject { ["ready"] = true };
                break;
            case "quick_restart":
                response["payload"] = QuickRestartPatches.TryQuickRestartFromOverlay();
                break;
            case "apply_settings":
                AndroidSettingsBridge.InvalidateCache();
                CompanionSettingsRuntime.ApplyAfterChange(request["keys"] as JsonArray);
                response["payload"] = new JsonObject { ["applied"] = true };
                break;
            case "inspector.roots":
                response["payload"] = ReflectionInspector.ListRoots();
                break;
            case "inspector.members":
                response["payload"] = ReflectionInspector.ListMembers(request["path"]?.GetValue<string>() ?? "");
                break;
            case "inspector.set":
            {
                var writable = AndroidSettingsBridge.GetBool("android_dev_inspector_writable", false);
                if (!writable)
                {
                    response["ok"] = false;
                    response["error"] = "Inspector write mode is disabled.";
                    break;
                }
                var path = request["path"]?.GetValue<string>() ?? "";
                var value = request["value"];
                response["payload"] = ReflectionInspector.SetValue(path, value);
                break;
            }
            default:
                response["ok"] = false;
                response["error"] = "Unknown op: " + op;
                break;
        }
        return response;
    }
}
