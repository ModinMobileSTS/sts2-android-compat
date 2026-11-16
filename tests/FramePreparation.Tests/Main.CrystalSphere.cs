using System.IO;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using STS2Mobile.Android;
using STS2Mobile.Patches;

public partial class Main
{
    private async Task CheckCrystalSphereReveal(NGame game)
    {
        using var reader = new StreamReader(typeof(Main).Assembly.GetManifestResourceStream("CrystalSphereRevealShader"));
        var variant = new Shader { Code = reader.ReadToEnd() };
        variant.TakeOverPath("res://shaders/mobile_compat/scry_reveal_compat.gdshader");
        foreach (var enabledAtEntry in new[] { true, false })
        {
            AndroidSettingsBridge.Enabled = enabledAtEntry;
            ShaderCompatibilityPatches.RefreshSettings();
            var viewport = new SubViewport
            {
                Size = new Vector2I(220, 220), Disable3D = true, World2D = new World2D(),
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            };
            game.AddChild(viewport);
            try
            {
                viewport.AddChild(new ColorRect { Size = new Vector2(220, 220), Color = new Color(0.1f, 0.9f, 0.2f) });
                var mask = new NCrystalSphereMask { Size = new Vector2(220, 220), Material = CrystalSphereMaterial(variant) };
                viewport.AddChild(mask);
                await RequireCrystalSpherePixels(viewport, firstRevealed: false, secondRevealed: false);

                mask._Process(1.0);
                mask.RevealCell(5, 5);
                mask._Process(1.0);
                await RequireCrystalSpherePixels(viewport, firstRevealed: true, secondRevealed: false);

                AndroidSettingsBridge.Enabled = true;
                ShaderCompatibilityPatches.RefreshSettings();
                await RequireCrystalSpherePixels(viewport, firstRevealed: true, secondRevealed: false);
                mask.RevealCell(3, 5);
                mask._Process(1.0);
                await RequireCrystalSpherePixels(viewport, firstRevealed: true, secondRevealed: true);

                AndroidSettingsBridge.Enabled = false;
                ShaderCompatibilityPatches.RefreshSettings();
                AndroidSettingsBridge.Enabled = true;
                ShaderCompatibilityPatches.RefreshSettings();
                await RequireCrystalSpherePixels(viewport, firstRevealed: true, secondRevealed: true);
            }
            finally
            {
                viewport.Free();
                AndroidSettingsBridge.Enabled = true;
                ShaderCompatibilityPatches.RefreshSettings();
            }
        }
        GD.Print("PASS: Crystal Sphere reveals remain visible with compatibility enabled at entry or after an earlier reveal, including repeated settings changes.");
    }

    private static ShaderMaterial CrystalSphereMaterial(Shader variant)
    {
        var shader = new Shader { Code = variant.Code };
        shader.TakeOverPath("res://shaders/scry_reveal.gdshader");
        var material = new ShaderMaterial { Shader = shader };
        var fog = new Color(0.2f, 0.2f, 0.8f);
        material.SetShaderParameter("borderColor", fog);
        material.SetShaderParameter("colors", new[] { fog, fog });
        material.SetShaderParameter("circleData", new[] { new Vector4(0, 2, 1, 0.5f) });
        material.SetShaderParameter("circles", 1);
        material.SetShaderParameter("outerCircle", -1);
        material.SetShaderParameter("uvMargin", 0.0f);
        material.SetShaderParameter("gridFadeParams", new Vector3[121]);
        material.SetShaderParameter("time", 0.0f);
        using var noiseImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        noiseImage.Fill(Colors.White);
        var noise = ImageTexture.CreateFromImage(noiseImage);
        material.SetShaderParameter("noiseTex1", noise);
        material.SetShaderParameter("noiseTex2", noise);
        return material;
    }

    private async Task RequireCrystalSpherePixels(SubViewport viewport, bool firstRevealed, bool secondRevealed)
    {
        await RenderWarmupFrame();
        using var image = viewport.GetTexture().GetImage();
        RequireCrystalSpherePixel(image.GetPixel(110, 110), firstRevealed);
        RequireCrystalSpherePixel(image.GetPixel(70, 110), secondRevealed);
        RequireCrystalSpherePixel(image.GetPixel(150, 110), revealed: false);
    }

    private static void RequireCrystalSpherePixel(Color pixel, bool revealed)
    {
        Require(revealed
                ? pixel.R < 0.2f && pixel.G > 0.8f && pixel.B < 0.3f
                : pixel.R < 0.3f && pixel.G < 0.3f && pixel.B > 0.7f,
            $"Crystal Sphere cell must {(revealed ? "show its contents" : "remain covered")}; rendered {pixel}.");
    }
}
