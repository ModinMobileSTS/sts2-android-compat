using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public partial class Main
{
    // Optional, read-only gate against locally supplied commercial DLLs. Never
    // load their types into the Godot process or execute game static constructors.
    private static void CheckOriginalContracts()
    {
        var references = Environment.GetEnvironmentVariable("STS2_FRAME_REFERENCE_DLLS");
        if (string.IsNullOrWhiteSpace(references)) return;
        foreach (string path in references.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var types = assembly.MainModule.Types;
            var session = types.Single(type => type.FullName == "MegaCrit.Sts2.Core.Assets.AssetLoadingSession");
            foreach (string name in new[] { "FinalizeLoading", "ProcessLoadingQueue", "CheckLoadingStatus", "ProcessVfxQueue" })
            {
                var method = session.Methods.Single(candidate => candidate.Name == name);
                var il = method.Body.Instructions;
                int index = -1;
                for (int i = 0; i < il.Count; i++)
                    if (il[i].Operand is MethodReference call && call.Name == "TryDequeue" && call.DeclaringType.FullName.StartsWith("System.Collections.Generic.Queue`1")) { index = i; break; }
                Require(method.ReturnType.FullName == "System.Void" && !method.Body.HasExceptionHandlers && index >= 3
                    && il[index - 3].OpCode == OpCodes.Ldarg_0 && il[index - 2].OpCode == OpCodes.Ldfld
                    && (il[index - 1].OpCode == OpCodes.Ldloca || il[index - 1].OpCode == OpCodes.Ldloca_S),
                    $"Unsupported original queue guard shape: {path}: {name}");
            }
            foreach (var spec in new[] { ("NDamageNumVfx", "AnimVfx", "_tween"), ("NHitSparkVfx", "FlashAndFree", "_creatureNode"), ("NShivThrowVfx", "PlaySequence", "_cts") })
            {
                var type = types.Single(candidate => candidate.FullName == "MegaCrit.Sts2.Core.Nodes.Vfx." + spec.Item1);
                Require(type.Methods.Any(method => method.Name == spec.Item2 && method.ReturnType.FullName == "System.Threading.Tasks.Task")
                    && type.Fields.Any(field => field.Name == spec.Item3), $"Unsupported original playback/reset shape: {path}: {spec.Item1}");
                Require(type.Methods.Where(method => method.IsStatic && method.Name == "Create" && method.HasBody)
                    .Any(method => method.Body.Instructions.Any(instruction => instruction.Operand is GenericInstanceMethod call
                        && call.Name == "Instantiate" && call.DeclaringType.FullName == "Godot.PackedScene"
                        && call.GenericArguments.Count == 1 && call.GenericArguments[0].FullName == type.FullName)),
                    $"Original VFX factory lacks expected instantiation: {path}: {spec.Item1}");
            }
            var shiv = types.Single(type => type.Name == "NShivThrowVfx");
            Require(shiv.Fields.Any(field => field.Name == "_modulateParticles"
                && field.FieldType.FullName == "Godot.Collections.Array`1<Godot.GpuParticles2D>"), $"Unsupported original tint shape: {path}");
            Godot.GD.Print($"PASS original queue/VFX contracts: {path}");
        }
    }
}
