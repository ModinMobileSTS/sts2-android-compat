# Android port compatibility MOD

This directory contains the first editable skeleton for the extracted Android
compatibility MOD / Harmony patcher. It is intentionally not a copy of the old
full game source.

Current implementation (`STS2AndroidPortCompat`):

- `ModEntry` exposes the same unmanaged entrypoints used by the reference
  launcher (`InitializeGodotSharp`, `Apply`).
- `PlatformPatches` disables desktop Steam/Sentry/platform paths.
- `ReleaseInfoPatches` reads `release_info.json` from the imported private
  payload at `OS.GetDataDir()/game/release_info.json`.
- `AndroidSettingsBridge` reads extra-settings JSON from
  `OS.GetDataDir()/default/1/settings.save` without requiring PC `SettingsSave`
  to contain Android-only fields.
- `AndroidSettingsPatches` maps companion JSON fields that also exist in the PC
  `SettingsSave` (`aspect_ratio`, `vsync`, `msaa`, `fps_limit`, `fullscreen`),
  maps companion `mod_settings.mods_enabled` / `mod_list` / legacy
  `disabled_mods` into the runtime `ModSettings`, and merges Android-only JSON
  keys back after PC `SettingsSave` serialization would drop them.
- `DisplaySettingsPatches` applies Android-only companion fields for FPS, global
  content scale, UI font scale, and landscape orientation. It is the sole
  coordinator for root-window `ContentScaleMode`, `ContentScaleAspect`, and
  `ContentScaleSize`; logical layout always uses `CanvasItems`, with the ownership
  order `FixedAspect > UiScaleAuto`. Auto uses the UI-scale target and fixed
  aspect uses its corresponding fixed target. `fullscreen_render_size` never owns
  or replaces that logical target and Java no longer forwards it as Godot
  `--resolution`; changing it in the in-game settings immediately resizes only the
  root renderer render target. After all high-level `ContentScale*` setters finish,
  the coordinator applies `RenderingServer.ViewportSetRenderDirectToScreen(false)`,
  `ViewportSetSize()`, and `ViewportSetGlobalCanvasTransform()`. The scene `Window`,
  its input transform, and the Android `Surface` stay unchanged. Do not use
  `SurfaceHolder.setFixedSize()` or `ViewportAttachToScreen()` for this path.
  `0x0` restores both the native attachment-sized render target and the base canvas
  transform. A non-zero preset is a minimum reference rectangle: the effective
  target keeps the current native attachment aspect and uses Expand-style coverage
  (for example, native `2400x1080` plus `1280x720` becomes `1600x720`). The custom
  longest-dimension cap is `max(4096, native longest dimension)`. Root-window
  `SizeChanged`, application resume, and consistency repair reapply the renderer
  state after logical setters. Ownership is published before
  any compare-before-set Window mutation, reentrant requests are coalesced, and
  application resume schedules one deferred runtime apply instead of rebuilding the
  viewport from focus notifications. `UiScalePatches` only supplies the Auto target
  and requests a single-flight recalculation; it never writes `ContentScale*` directly.
  `global_scale` remains an independent `ContentScaleFactor` under every owner,
  and UI font scale remains independent.
  Each resume generation performs one deferred consistency check and at most one
  compare-before-set repair; stale targets are rejected by revision and a failed
  final check only logs a warning instead of entering a viewport rebuild loop.
- `MobileHandLayoutPatches` applies the companion `show_more_hand_card_text` /
  `show_more_hand_card_text_lift_height_percent` hand lift as a Harmony
  post-layout offset without rebuilding the game body.
- `DevTools/` hosts the file-based Java bridge for the in-game overlay. It is
  started independently from optional version-specific feature patches, writes a
  `launcher/devtools/host.json` ready marker, and answers protocol-2 requests in
  their own atomic `response-<uuid>.json` files (while retaining legacy
  `response.json` compatibility): reflection inspector, collapsible Godot scene
  tree, nested Godot object / node property inspection, and temporary GDScript execution
  with non-Nil result capture,
  companion settings runtime apply, and overlay quick-restart.
- `QuickRestartPatches` adds the built-in Android retry button on the pause menu
  when `quick_sl_enabled` is true and no external Quick Restart UI mod is loaded;
  it waits for pending run-save work, awaits saved-run setup before loading the
  new run, and fades back in on failure so async restart errors do not leave a
  permanent black transition screen.
- `ExternalSettingsPatches` adds a fallback in-game settings row that opens the
  Java companion settings shell, redirects game Quit back to the settings shell,
  and applies the companion `pending_unlock_all.flag` command.
- `ModLoaderPatches` redirects local mods to `OS.GetDataDir()/mods` and skips
  Steam mod enumeration.
- `ShaderCompatibilityPatches` loads `port_compat.pck` and applies the mobile
  shader replacements copied from the old port when
  `shader_compatibility_mode` is enabled; it intentionally keeps the original
  `canvas_group_mask_blur.gdshader` card/Ancient-card face shader and does not
  ship the old mobile substitute because it can render Ancient card faces solid
  white.
- `TouchInputPatches` adds the first touch-friendly card-play cancellation path
  for releases outside the play zone / untargeted releases.
- `MobileTapPreviewPatches` adds a first-pass tap-to-lift card preview flow using
  companion `touch_lift_preview` / `touch_lift_retap_action` settings.
- `AndroidInputCompatPatches` bridges Android back-button, two-finger inspect
  right-click, and trigger-axis controller compatibility into original input.
- `LanMultiplayerPatches` bridges companion LAN settings while leaving the
  original `MessageTypes` ID assignment and `NetMessageBus`
  serialization/deserialization untouched. It adds configured compatibility
  mod names to multiplayer checks, honors persistent/custom LAN player IDs,
  replaces the no-Steam join screen with host/port input, and hosts ENet games
  with the configured player capacity.

Build locally from the parent repository after configuring `.env`:

```bash
../tools/android/build-port-mod.sh
```

Or build the schema-2 family compatibility pack from this submodule with local environment variables:

```bash
export DOTNET_BIN=/path/to/dotnet
export STS2_ORIGINAL_V1080_REFERENCE_DIR=/path/to/original-v0.108.0/bin/Debug
./tools/build-compat-matrix.sh --target v0.108.0

export STS2_ORIGINAL_V1090_REFERENCE_DIR=/path/to/original-v0.109.0/bin/Debug
./tools/build-compat-matrix.sh --target v0.109.0
```

`tools/build-compat-pack.sh` is the legacy schema-1 path; use it only when also providing a matching `COMPAT_MANIFEST`.

The patched Godot runtime expects `STS2Mobile.dll` / `STS2Mobile.ModEntry`; the parent build script builds this skeleton under that assembly name and copies it into `android/assets/dotnet_bcl/`.
