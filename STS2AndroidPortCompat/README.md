# STS2AndroidPortCompat

The patched Godot Android runtime currently looks for assembly
`STS2Mobile.dll` and type `STS2Mobile.ModEntry`. For compatibility, the build
entrypoint project is `STS2Mobile.csproj`, which compiles this source tree into
that assembly name. `STS2AndroidPortCompat.csproj` is kept as a descriptive
project name for local IDE use, but it is not the runtime assembly name.

Build and stage into the Android shell:

```bash
tools/android/build-port-mod.sh
```

Compile against configured reference DLLs (from the parent repository `.env`):

```bash
REFERENCE_FLAVOR=original-v0.106.1 tools/android/build-port-mod.sh
```

Compile against the original PC `sts2.dll` to catch accidental dependencies on
old-port-only game-source additions. When running `dotnet` directly, pass the
reference directory explicitly:

```bash
"$DOTNET_BIN" build port-mod/STS2AndroidPortCompat/STS2Mobile.csproj \
  -p:ReferenceFlavor=original \
  -p:CompatReferenceDir="$STS2_ORIGINAL_V103_REFERENCE_DIR" -v:q
```


Startup/mod compatibility notes:

- `ModEntry` now keeps the working `StS2-Launcher_Mod_Manager` patch order as
  the baseline: diagnostics, BaseLib AssemblyLoad hook, ModelDb/platform/
  release-info/settings/layout/input, LAN, then ModManager scan redirection.
- `EarlyLocalizationFallbackPatches` protects the Android/Mono eager static
  constructor path while user mods are running `Harmony.PatchAll`. If a game UI
  type formats a `LocString` before `LocManager.Initialize`, the compat layer
  returns a stable key fallback until normal localization initialization has
  completed. Do not move full `LocManager.Initialize` earlier; mod localization
  hooks depend on the original PC ordering.
- `DeferredModPatchQueue` covers the adjacent case where a user mod patches an
  STS2 Godot/UI type whose static fields directly read `LocManager.Instance` or
  other not-yet-essential state. During each user-mod initializer/PatchAll it
  queues patches for `sts2` UI/Godot targets with type initializers, then replays
  them after `LocManager`, `ModelDb`, and model-id initialization finish.
  Model-type patches are intentionally not deferred, because many mods need
  their `ModelDb.Init` prefixes installed before the model construction phase.
- `BaseLibCompatPatches` is intentionally the same degraded-mode workaround as
  the reference launcher: `BaseLib.Utils.Patching.AsyncMethodCall.Create` is
  prefix-disabled when BaseLib loads, so BaseLib's async hook state-machine
  surgery is skipped while the rest of BaseLib can load.
- `ModLoaderPatches` still rewrites `ModManager.Initialize` at the original
  `Path.Combine(..., "mods")` IL site, letting the game perform its normal
  recursive manifest scan, dependency sort, settings filtering, DLL/PCK load,
  and initializer/PatchAll flow.
- The Java shell normalizes imported `mod_manifest.json` to a `<ModId>.json`
  alias because the current PC `ModManager` scans any `*.json` manifest and
  loads payloads by `<ModId>.dll` / `<ModId>.pck`.

Overlay resource pack:

```bash
tools/android/make-port-overlay-pck.py
```

`tools/android/build-port-mod.sh` runs this automatically and stages
`android/assets/port_compat.pck`, which `GodotApp` extracts to
`OS.GetDataDir()/port_compat.pck` before the compat DLL loads it.

LAN multiplayer compatibility is implemented as Harmony patches in
`Patches/LanMultiplayerPatches.cs`. It reads `lan_multiplayer_enabled`,
`lan_compatibility_mod_names`, `lan_use_custom_player_id`,
`lan_use_custom_platform_player_id`, `lan_custom_player_id`, `lan_join_host`,
`lan_join_port`, `max_multiplayer_enabled`, and `max_multiplayer_players` from
companion settings instead of adding Android-only fields to the imported PC game
assembly. `lan_multiplayer_enabled` is the local multiplayer patch master switch;
when it is off, or when `sts2_lan_connect` / STS2 Game Lobby is already loaded,
the whole local LAN patch set is skipped to avoid protocol conflicts with that
MOD.

Local mod enable/disable compatibility is handled in
`Patches/AndroidSettingsPatches.cs`: companion `mod_settings.mods_enabled`,
`mod_list[]`, and legacy `disabled_mods[]` are projected into the runtime
`ModSettings` shape used by the current reference DLL and the original PC DLL.

Mobile hand text visibility compatibility now has a small first patch in
`Patches/MobileHandLayoutPatches.cs`: after the original hand layout runs, it
lifts visible hand cards by the companion `show_more_hand_card_text` percentage
instead of relying on Android-only `SettingsSave` fields in the game body.

Quick-save/load compatibility now has a built-in retry patch in
`Patches/QuickRestartPatches.cs`: when companion `quick_sl_enabled` is true, the
pause menu gets an Android retry button unless an external Quick Restart UI mod
is already loaded. The restart path waits for pending run-save work and awaits
saved-run setup before `NGame.LoadRun()` so `MapSelectionSynchronizer` belongs to
the new `RunState`; if a post-fadeout error still occurs, it attempts to fade
back in before showing the error popup.

Mobile tooltip compatibility is handled by `Patches/MobileTooltipPatches.cs`.
Companion `mobile_tooltip_mode` defaults to `immediate` (PC behavior), can be
set to `long_press` to hide hover tooltips until the same touch is held for
`mobile_tooltip_long_press_ms` (default 1000 ms), or `hidden` to suppress normal
hover tooltips while keeping explicit inspect/detail screens visible.

Touch preview compatibility now has a first-pass patch in
`Patches/MobileTapPreviewPatches.cs`: when companion `touch_lift_preview` is
true, tapping a playable hand card pins its hover preview; a second tap follows
`touch_lift_retap_action` (`put_down`, `play`, or `none`).

Android input compatibility now has a first-pass patch in
`Patches/AndroidInputCompatPatches.cs`: it maps the Android back action to game
cancel/pause, emits synthetic right-clicks for two-finger inspect when
`mobile_two_finger_inspect` is enabled, and normalizes trigger-axis controller
input on Android.
