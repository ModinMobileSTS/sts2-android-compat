using Godot;

namespace MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;

// Synthetic game shape: the script caches its material and drives cell fades;
// the compatibility patch and the rendered pixels use real Godot resources.
public partial class NCrystalSphereMask : ColorRect
{
    private ShaderMaterial _material;
    private readonly Godot.Collections.Array<Vector3> _cells = new();
    private float _time;

    public override void _Ready()
    {
        _material = (ShaderMaterial)Material;
        for (var i = 0; i < 121; i++)
            _cells.Add(Vector3.Zero);
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        _material.SetShaderParameter("time", _time);
    }

    public void RevealCell(int x, int y)
    {
        _cells[y * 11 + x] = new Vector3(1, 0, _time);
        _material.SetShaderParameter("gridFadeParams", _cells);
    }
}
