using System;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Patches;

// Only the native resource loader runs in the background. All requests, resource
// retrieval and scene operations stay on the Godot thread. Share one in-flight
// request across the compat warmup passes: VFX often share external resources.
internal static class AndroidResourcePreloader
{
    private static bool _loading;

    internal static async Task<Resource> LoadAsync(string path, ResourceLoader.CacheMode cacheMode)
    {
        if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            throw new InvalidOperationException("Android resource preparation must start on the Godot thread.");
        if (Engine.GetMainLoop() is not SceneTree tree)
            throw new InvalidOperationException("Android resource preparation requires a running scene tree.");

        while (_loading)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        _loading = true;
        try
        {
            Error error = ResourceLoader.LoadThreadedRequest(path, "", useSubThreads: false, cacheMode);
            if (error != Error.Ok)
                throw new InvalidOperationException($"Threaded resource request failed: {path} ({error}).");

            while (true)
            {
                var status = ResourceLoader.LoadThreadedGetStatus(path);
                if (status == ResourceLoader.ThreadLoadStatus.Loaded)
                    return ResourceLoader.LoadThreadedGet(path);
                if (status != ResourceLoader.ThreadLoadStatus.InProgress)
                {
                    // Balance each accepted request, including failed loads. Never
                    // retrieve an in-progress resource: that would block the frame.
                    ResourceLoader.LoadThreadedGet(path);
                    throw new InvalidOperationException($"Threaded resource load failed: {path} ({status}).");
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // Bound cached/immediately-ready batches too. A single native operation is
    // not preemptible; this only decides whether another item may start now.
    internal struct FrameBudget
    {
        private ulong _frame;
        private ulong _started;
        private int _items;
        private readonly int _maximumItems;

        public FrameBudget() : this(8) { }

        internal FrameBudget(int maximumItems)
        {
            _frame = Engine.GetProcessFrames();
            _started = Time.GetTicksUsec();
            _items = 0;
            _maximumItems = maximumItems;
        }

        internal bool ShouldYield()
        {
            ResetForFrame(Engine.GetProcessFrames());
            return ++_items >= _maximumItems || Time.GetTicksUsec() - _started >= 2000;
        }

        internal bool TryBeginItem(ulong frame)
        {
            ResetForFrame(frame);
            if (_items >= _maximumItems || Time.GetTicksUsec() - _started >= 2000)
                return false;
            _items++;
            return true;
        }

        private void ResetForFrame(ulong frame)
        {
            if (frame == _frame) return;
            _frame = frame;
            _started = Time.GetTicksUsec();
            _items = 0;
        }
    }
}
