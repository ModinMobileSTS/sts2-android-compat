using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace MegaCrit.Sts2.Core.Assets;

// Independent queue fixture with the stock loop shapes. Loading and failures use
// the real native ResourceLoader; no commercial game code or resources required.
public class AssetLoadingSession
{
    private readonly Queue<string> _toLoad = new();
    private readonly Queue<string> _loading = new();
    private readonly Queue<string> _finalizing = new();
    private readonly Queue<string> _vfxScenes = new();
    private string _vfxPath;
    private readonly TaskCompletionSource<bool> _completion = new();
    public readonly Dictionary<string, Resource> Loaded = new();
    public readonly List<string> Errors = new();
    public Task Completion => _completion.Task;
    public int Pending => _toLoad.Count + _loading.Count + _finalizing.Count + _vfxScenes.Count + (_vfxPath == null ? 0 : 1);

    public AssetLoadingSession(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            (path.Contains("/vfx/") ? _vfxScenes : _toLoad).Enqueue(path);
    }
    public void Process()
    {
        FinalizeLoading();
        ProcessLoadingQueue();
        CheckLoadingStatus();
        if (_toLoad.Count + _loading.Count + _finalizing.Count == 0) ProcessVfxQueue();
        if (Pending == 0) _completion.TrySetResult(true);
    }
    private void FinalizeLoading()
    {
        while (_finalizing.Count != 0)
        {
            if (!_finalizing.TryDequeue(out var path)) throw new System.InvalidOperationException("Lost finalizing item");
            Loaded.Add(path, ResourceLoader.LoadThreadedGet(path));
        }
    }
    private void ProcessLoadingQueue()
    {
        while (_loading.Count < 128 && _toLoad.TryDequeue(out var path))
        {
            if (ResourceLoader.LoadThreadedRequest(path) == Error.Ok) _loading.Enqueue(path);
            else Errors.Add(path);
        }
    }
    private void CheckLoadingStatus()
    {
        int count = _loading.Count;
        for (int i = 0; i < count; i++)
        {
            if (!_loading.TryDequeue(out var path)) break;
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.Loaded) _finalizing.Enqueue(path);
            else if (status == ResourceLoader.ThreadLoadStatus.InProgress) _loading.Enqueue(path);
            else
            {
                ResourceLoader.LoadThreadedGet(path);
                var fallback = ResourceLoader.Load(path);
                if (fallback != null) Loaded.Add(path, fallback);
                else Errors.Add(path);
            }
        }
    }
    private void ProcessVfxQueue()
    {
        if (_vfxPath != null)
        {
            var status = ResourceLoader.LoadThreadedGetStatus(_vfxPath);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress) return;
            var result = ResourceLoader.LoadThreadedGet(_vfxPath);
            if (result == null) Errors.Add(_vfxPath);
            else Loaded.Add(_vfxPath, result);
            _vfxPath = null;
            return;
        }
        while (_vfxScenes.TryDequeue(out var path))
        {
            if (ResourceLoader.LoadThreadedRequest(path) == Error.Ok) { _vfxPath = path; break; }
            Errors.Add(path);
        }
    }
}
