using Godot;

namespace MegaCrit.Sts2.Core.Nodes.Combat;

// Synthetic factory with the original Instantiate-before-entering-tree boundary.
public partial class NEnergyCounter : Control
{
    public static NEnergyCounter Create(PackedScene scene) => scene.Instantiate<NEnergyCounter>();
}
