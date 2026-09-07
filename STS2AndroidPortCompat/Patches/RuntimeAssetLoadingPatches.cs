using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

// Leave queue ownership, retries, cache writes, logging and completion in STS2.
// Yield only before removing the next item, never by pretending a queue is empty.
internal static class RuntimeAssetLoadingPatches
{
    private static readonly ConditionalWeakTable<AssetLoadingSession, Budget> Budgets = new();
    private static Harmony _harmony;
    private static bool _installed;
    private static readonly string[] Phases = { "FinalizeLoading", "ProcessLoadingQueue", "CheckLoadingStatus", "ProcessVfxQueue" };

    internal static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(RuntimeAssetLoadingPatches), nameof(GameReadyPostfix)));
    }

    private static void GameReadyPostfix()
    {
        if (_installed || OS.GetName() != "Android") return;
        _installed = true;
        try
        {
            var methods = Phases.Select(name => AccessTools.DeclaredMethod(typeof(AssetLoadingSession), name)
                ?? throw new MissingMethodException(typeof(AssetLoadingSession).FullName, name)).ToArray();
            foreach (var method in methods) Validate(method, PatchProcessor.GetOriginalInstructions(method));
            foreach (var method in methods)
                _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RuntimeAssetLoadingPatches), nameof(Transpiler)));
            _harmony.Patch(methods[3], prefix: new HarmonyMethod(typeof(RuntimeAssetLoadingPatches), nameof(VfxPrefix)));
            PatchHelper.Log("Runtime resource sessions budgeted: 2ms cooperative frame budget, at most 8 items per phase; original completion and failures retained.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Runtime resource session budget unavailable: {exception}");
        }
    }

    private static int FindDequeue(IList<CodeInstruction> code)
    {
        for (int i = 3; i < code.Count; i++)
            if (code[i].operand is MethodInfo method && method.DeclaringType == typeof(Queue<string>) && method.Name == "TryDequeue")
                return i;
        return -1;
    }

    private static void Validate(MethodBase method, IList<CodeInstruction> code)
    {
        int index = FindDequeue(code);
        if (method is not MethodInfo { ReturnType: var result } || result != typeof(void)
            || method.GetMethodBody().ExceptionHandlingClauses.Count != 0 || index < 3
            || code[index - 3].opcode != OpCodes.Ldarg_0 || code[index - 2].opcode != OpCodes.Ldfld
            || code[index - 1].opcode != OpCodes.Ldloca_S && code[index - 1].opcode != OpCodes.Ldloca)
            throw new InvalidOperationException($"Unknown resource queue loop shape: {method}");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
    {
        var code = instructions.ToList();
        Validate(__originalMethod, code);
        int before = FindDequeue(code) - 3;
        int phase = Array.IndexOf(Phases, __originalMethod.Name);
        var proceed = generator.DefineLabel();
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(code[before].labels);
        code[before].labels.Clear();
        code[before].labels.Add(proceed);
        code.InsertRange(before, new[]
        {
            first,
            new CodeInstruction(OpCodes.Ldc_I4, phase),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RuntimeAssetLoadingPatches), nameof(Allow))),
            new CodeInstruction(OpCodes.Brtrue, proceed),
            new CodeInstruction(OpCodes.Ret),
        });
        return code;
    }

    private static bool VfxPrefix(AssetLoadingSession __instance) => Allow(__instance, 3);
    private static bool Allow(AssetLoadingSession session, int phase) => Budgets.GetValue(session, static _ => new Budget()).Take(phase);

    private sealed class Budget
    {
        private ulong _frame = ulong.MaxValue;
        private int _finalized, _requested, _checked, _vfx;
        private AndroidResourcePreloader.FrameBudget _time = new(24);

        internal bool Take(int phase)
        {
            ulong frame = Engine.GetProcessFrames();
            if (frame != _frame)
            {
                _frame = frame;
                _finalized = _requested = _checked = _vfx = 0;
            }
            // Separate item quotas let status checks run even when a large request
            // queue is present. All phases still share the same 2ms wall budget.
            ref int count = ref (phase == 0 ? ref _finalized : ref phase == 1 ? ref _requested : ref phase == 2 ? ref _checked : ref _vfx);
            if (count >= 8 || !_time.TryBeginItem(frame)) return false;
            count++;
            return true;
        }
    }
}
