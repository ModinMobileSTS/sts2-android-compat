# DeferredModPatchQueue synthetic regression

This test reproduces the Harmony entrypoint gap from launcher issue #33 without
shipping or compiling commercial game code. `GameFixture` deliberately builds a
small assembly named `sts2` with an `NDailyRunScreen`-shaped UI type whose static
constructor fails before an explicit essential-startup marker.

The runner links the production `DeferredModPatchQueue.cs` and verifies that:

- `Harmony.PatchAll()` jobs for STS2 UI/Godot types stay queued without running
  the dangerous static constructor;
- a model target from the same multi-target patch class remains immediate and is
  not applied twice;
- prefix, postfix, transpiler, finalizer, Harmony ID, priority/before ordering,
  and prepare/cleanup behavior survive replay;
- `HarmonyPrepare=false` still suppresses its target;
- one failed deferred job does not prevent a later job from replaying;
- the existing direct `CreateProcessor(...).Patch()` path still defers and only
  replays once.

Run from the compat repository:

```bash
tools/test-deferred-mod-patch-queue.sh
```

Use `--reference-dir DIR` when the parent launcher's packaged `0Harmony.dll` is
not available at `../android/assets/dotnet_bcl/`.
