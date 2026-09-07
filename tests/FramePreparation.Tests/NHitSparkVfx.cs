using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace MegaCrit.Sts2.Core.Nodes.Vfx;

public partial class NHitSparkVfx : Node2D
{
    public static PackedScene Scene;
    public TaskCompletionSource<bool> Finish;
    private Node _creatureNode;
    public static NHitSparkVfx Create(Node target)
    {
        var node = Scene.Instantiate<NHitSparkVfx>(PackedScene.GenEditState.Disabled);
        node._creatureNode = target;
        return node;
    }
    public override void _Ready()
    {
        if (_creatureNode is Node2D target) GlobalPosition = target.GlobalPosition;
        Finish = new();
        _ = FlashAndFree();
    }
    private async Task FlashAndFree() { await Finish.Task; this.QueueFreeSafely(); }
}
