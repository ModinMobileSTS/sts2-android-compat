using System.Threading;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using MegaCrit.Sts2.Core.Helpers;

namespace MegaCrit.Sts2.Core.Nodes.Vfx;

public partial class NShivThrowVfx : Node2D
{
    public static PackedScene Scene;
    [Export] private Array<GpuParticles2D> _modulateParticles = new();
    private CancellationTokenSource _cts;
    public TaskCompletionSource<bool> Finish;
    public static NShivThrowVfx Create(Vector2 position, Color tint)
    {
        var node = Scene.Instantiate<NShivThrowVfx>(PackedScene.GenEditState.Disabled);
        node.Position = position;
        node.ApplyTint(tint);
        return node;
    }
    public void ApplyTint(Color tint)
    {
        foreach (var particle in _modulateParticles)
        {
            particle.ProcessMaterial = (ParticleProcessMaterial)particle.ProcessMaterial.Duplicate();
            particle.ProcessMaterial.Set("color", tint);
        }
    }
    public void AddParticle(GpuParticles2D particle)
    {
        AddChild(particle);
        particle.Owner = this;
        _modulateParticles.Add(particle);
    }
    public override void _Ready() { Finish = new(); _ = PlaySequence(); }
    private async Task PlaySequence()
    {
        _cts = new();
        await Finish.Task.WaitAsync(_cts.Token);
        this.QueueFreeSafely();
    }
    public override void _ExitTree() { _cts?.Cancel(); _cts?.Dispose(); }
}
