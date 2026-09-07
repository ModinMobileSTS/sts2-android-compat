using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace MegaCrit.Sts2.Core.Nodes.Vfx;

// Synthetic playback; real scene instantiation, Ready/ExitTree and async contexts.
public partial class NDamageNumVfx : Node2D
{
    public static PackedScene Scene;
    public static bool TestLateRelease;
    public TaskCompletionSource<bool> Finish;
    public TaskCompletionSource<bool> Late;
    public string Text;
    private Tween _tween;

    public static NDamageNumVfx Create(Vector2 position, int value)
    {
        var node = Scene.Instantiate<NDamageNumVfx>(PackedScene.GenEditState.Disabled);
        node.Position = position;
        node.Text = value.ToString();
        return node;
    }
    public override void _Ready()
    {
        Finish = new();
        Late = new();
        _tween = CreateTween();
        _tween.TweenInterval(20);
        _ = AnimVfx();
    }
    private async Task AnimVfx()
    {
        if (TestLateRelease) _ = DelayedRelease(Late.Task);
        await Finish.Task;
        Modulate = Colors.Blue;
        Scale = Vector2.One * 3;
        this.QueueFreeSafely();
    }
    private async Task DelayedRelease(Task task)
    {
        await task;
        this.QueueFreeSafely();
    }
    public override void _ExitTree() { _tween?.Kill(); }
}
