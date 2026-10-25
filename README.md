# Android port compatibility MOD

This directory contains the first editable skeleton for the extracted Android
compatibility MOD / Harmony patcher. It is intentionally not a copy of the old
full game source.

Current implementation (`STS2AndroidPortCompat`):

- `ModEntry` exposes the same unmanaged entrypoints used by the reference
  launcher (`InitializeGodotSharp`, `Apply`).
- `PlatformPatches` disables desktop Steam/Sentry/platform paths.
- `RunHistoryPatches` keeps persisted `PlatformType.Steam` run histories readable
  without loading desktop `steam_api64`: only history-screen identity lookups use
  the Android/null-platform ID and cached-name fallback, while Steam transport,
  lobby, and invite behavior remains untouched.
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
- `MerchantLayoutPatches` animates the shop panel's anchor-relative offset against
  the inventory's live Control height, not the root `ContentScaleSize`. Global
  scale and UI scale can be applied before opening, after opening, or during the
  animation without a stale absolute Y target pushing the bottom row offscreen.
  Item sizes, purchase behavior, and the original easing remain unchanged; this
  does not shrink oversized content to fit arbitrarily high zoom levels.
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
  Runtime manifest aliases accept only file basenames and never follow symlink files or directories.
  Run `dotnet run --project tests/ModManifestAlias.Tests/ModManifestAlias.Tests.csproj`
  for isolated traversal, Unicode-name and symlink regressions (no game assemblies required).
- `ModelDbInitPatch` keeps early vanilla models in a shadow registry until
  phase 1. Canonical-dictionary keyed reads cover generic, Type, ID and
  category lookups without patching shared generic method bodies. Existing
  canonical values and original casts/errors take precedence. Publication
  preserves object identity and removes the temporary read prefixes.
- `DeferredModPatchQueue` protects Android/Mono from user-MOD patches that
  eagerly initialize STS2 UI/Godot/model types or read ModelDb before
  essential startup. It covers direct `PatchProcessor.Patch()`, the
  per-target private `PatchClassProcessor.ProcessPatchJob()` path used by
  `Harmony.PatchAll()`, and `HarmonyTargetMethods` / `HarmonyTargetMethod`
  factories whose target discovery reads ModelDb. It also defers resource
  types whose static initialization reads models or consumes modded pools,
  including helper and iterator calls. ID-only discovery and safe registration
  remain immediate; unsafe jobs retain their original Harmony owner, patch lists,
  ordering, and per-target prepare/cleanup flow and replay once after
  model/network type initialization. A synthetic `sts2` fixture regression
  is available through `tools/test-deferred-mod-patch-queue.sh`; it covers
  direct patches, PatchAll jobs, target factories, resource cctors, pool
  registration/freeze boundaries, failure isolation and duplicate-flush idempotence.
- `ShaderCompatibilityPatches` loads `port_compat.pck` and applies a finite, explicit
  resource-path allowlist of handwritten Godot 4 canvas-item variants when
  `shader_compatibility_mode` is enabled. Screen-sensitive variants retain their
  `SCREEN_UV`/screen-texture path and must not degrade to a solid white or black
  output; this is a visual contract, not a claim of GPU-identical output. A loaded
  replacement `Shader` may be reused, but every CanvasItem receives its own
  duplicated `ShaderMaterial`, so per-node uniforms and the original shared
  material stay isolated. The current variant set includes card portrait and
  non-Ancient canvas-group blur, water reflection, flipbook/row-flipbook,
  screen chromatic aberration, HSV, scry reveal, rest-site light, Vantom oil,
  and hash-allowlisted generated fire/VFX variants. `SceneTree.NodeAdded` only
  observes CanvasItem nodes; Spine slot materials and material assignments that
  happen outside the deferred NodeAdded batch are not automatically covered.
  Known `AssetCache.GetMaterial()` and `NCard.Reload()` repair paths explicitly
  bind replacement material copies; the current integration exposes no generic
  late-material helper, so callers must not assume arbitrary dynamic assignments
- The same shader-compatibility switch enables a narrow Android/Maleoon workaround
  only with the actual `gl_compatibility` renderer. At `NEnergyCounter.Create()`
  return, before tree entry, the known built-in Ironclad front energy-ember emitter
  receives a shallow `ParticleProcessMaterial` copy with only turbulence disabled.
  Original scene identities, embedded material provenance, one-shot playback and
  absence of a custom particle script are required. Shared materials, other emitters,
  particle counts/timing and combat logic stay unchanged; custom MOD resources and
  Vulkan/non-Maleoon runtimes are excluded. Repeated settings application is
  idempotent; disabling stops future replacements but restoring existing copies
  requires restarting the game. Native boundaries are covered by
  `tests/FramePreparation.Tests`; desktop OpenGL does not certify Huawei drivers.
- Inline VisualShader resources with no stable resource path are handled only by
  an exact generated-code SHA-256 allowlist plus a built-in source identity prefix
  (`res://scenes/`, `res://images/`, or `res://shaders/`). `res://mods/`,
  `res://user/`, and other roots are excluded; a hash match alone is not enough.
  The allowlist is audited against the v0.111.0 generated code and does not
  provide a generic arbitrary-inline fallback. It intentionally keeps the original
  `canvas_group_mask_blur.gdshader` card/Ancient-card face shader and does not
  ship the old mobile substitute because it can render Ancient card faces solid
  white.
- `AndroidSettingsMerge` preserves `android_compat_pack_enabled` when the game
  serializes settings, including an explicit `false`. A later launch must not
  silently re-enable compatibility because the PC serializer omitted this key.
  Consumer round-trip: `tests/AndroidSettingsMerge.Tests`.
- `TouchInputPatches` adds the first touch-friendly card-play cancellation path
  for releases outside the play zone / untargeted releases.
- `MobileTapPreviewPatches` adds a first-pass tap-to-lift card preview flow using
  companion `touch_lift_preview` / `touch_lift_retap_action` settings.
- `MobileReactionButtonPatches` injects the Android reaction-wheel button while
  reusing the payload's reaction synchronizer. Android detection accepts either
  the mobile feature tag or the Android OS name, and networking lifecycle hooks
  refresh visibility immediately. Visible multiplayer lobby/player containers
  and ready/wait overlays keep the button available before the run starts.
  Because the compat assembly uses plain `Microsoft.NET.Sdk` without the game's
  Godot source-generated virtual callback dispatch, the dynamic button connects
  `Control.gui_input` and `Timer.timeout` signals explicitly; an `NGame._Input`
  postfix forwards active-pointer drag and release events. Button and wheel
  centers are resolved in viewport coordinates with the full CanvasItem
  transforms, then the wheel correction is converted back into its parent's
  coordinates. Before dispatch, that viewport center is also inverted through
  `NReactionContainer.GetGlobalTransformWithCanvas()` so the payload's
  `DoLocalReaction` receives control-space coordinates for both the local
  animation and existing normalized network message. On first show, the compat
  path captures every anchored wedge's live neutral position and refreshes the
  payload's `_defaultPosition` after responsive wheel-size changes; selection
  still moves one wedge radially, but deselection and a full sweep return all
  eight wedges to stable baselines. Diagnostics cover platform, setting,
  network, scene, geometry, press, wheel requested/actual centers, alignment
  error, dispatch viewport/control positions, stale payload wedge delta, reset
  correction, release/react, and failures while single-player stays hidden.
  The 250ms visibility timer queries a small `MobileReactionSurfaceTracker` index,
  not the scene tree. The index seeds once and follows `SceneTree.NodeAdded` /
  `NodeRemoved`; hidden ancestors and current scene/overlay ownership remain part
  of visibility checks. Disabling the setting or leaving the tree disconnects and
  clears the index; reentry seeds it again. Do not restore recursive `GetChildren()`
  polling: it allocates native-array/name wrappers even in single-player with the
  button hidden. Regression coverage lives in `tests/MobileReactionButton.Tests`.
- `AndroidInputCompatPatches` bridges Android back-button, two-finger inspect
  right-click, and trigger-axis controller compatibility into original input.
- `ExtendedMultiplayerRoomPatches` keeps the original multiplayer synchronizers
  authoritative while extending client room UI beyond the four prebuilt slots:
  treasure rooms create and lay out one holder per synchronized relic, use a
  bounded default focus target, spread award/fight hands around the screen, and
  rest sites create one ordered character container per player. A commercial-
  code-free synthetic regression is available through
  `tools/test-extended-multiplayer-rooms.sh`.
  Older payloads without `_relicContainer` use the existing `Container` child;
  default-focus protection does not depend on that optional field. Run the
  synthetic runner and native `FramePreparation.Tests` with both default and
  `-p:LegacyTreasureShape=true` shapes. These fixtures do not replace real-game
  multiplayer verification of animation/completion and network synchronizers.
- `LanMultiplayerPatches` bridges companion LAN settings while leaving the
  original `MessageTypes` ID assignment and `NetMessageBus`
  serialization/deserialization untouched. It adds configured compatibility
  mod names to multiplayer checks, honors persistent/custom LAN player IDs,
  replaces the no-Steam join screen with host/port input, and hosts ENet games
  with the configured player capacity. The v0.111 target passes
  `PeerVersionInfo.LocalDefault()` into the original host/client services and
  lets the original transport-level `HandshakeManager` own version, ModelDb
  hash, and MOD compatibility validation.
- In-game Android settings expose the launcher's display refresh-rate modes:
  `android_display_refresh_rate_mode=high/60hz/system`, with high refresh as the
  default. Changes call the Java Activity bridge immediately, and
  `AndroidSettingsMerge` preserves the string through the game's typed settings
  save. Legacy booleans migrate in the launcher; no legacy key is written back.
  A 60Hz request requires an exposed compatible target; follow-system clears the
  app's Window/Surface request. Neither mode changes the game's FPS cap or VSync.
- Layout scale subscriptions are owned by scene-node lifetime: detach on
  `TreeExiting`, restore on reentry, and ignore repeated Ready registration.
  Old rooms and event layouts no longer need a later scale change to be collectible.
- Main-menu scaling records neutral button/logo geometry per node. Repeated Ready
  or scale notifications do not accumulate logo offsets; returning to 100% restores
  anchors, offsets, grow directions, size flags and logo position without changing
  the root display-scale owner. Native coverage: `tests/FramePreparation.Tests`.
- Font scaling distinguishes inherited sizes from existing explicit overrides.
  Returning to 100% removes only scaler-created overrides and releases the old
  baseline, so later theme changes and scaling cycles use current font sizes.
  Existing or externally replaced overrides and auto-size bounds remain owned
  by their original caller; font fallback selection is unchanged.
- Disabling preload gates only `PreloadManager.LoadAssets`; the outer asset-set
  operation still performs cache and missed-set eviction. The resource disposal
  guard and protected warm-cache scope remain unchanged; no forced GC is added.
- Learned warm assets use one atomic schema-2 snapshot capped at 512 paths and
  scoped to the launch profile, payload/compat assemblies, and ordered loaded-MOD
  identities/file metadata. Unscoped legacy lists or mismatched contexts are relearned.
- Optional combat animation warmup constructs a fresh native `SpineSprite` via
  `ClassDB.Instantiate`, sharing read-only data/materials and copying the current
  skin attachment map and render properties. Spine owns its skeleton, state and
  mesh children: never `Duplicate()` then strip children, or free meshes separately.
  Scripts, game signals and child audio/particles are not copied. Temporary and
  preview-only bindings are released on the Godot main thread; source/shared
  bindings stay borrowed, and tree exit stops later sampling before root disposal.
  It never triggers or rewinds the live creature animator; trigger-driven VFX/audio
  remain outside animation warmup. Unsupported Spine APIs skip the preview. Existing
  animation/hit-effect settings and defaults are unchanged. Native rendered regression:
  `tests/SpineWarmup.Tests/run-native.sh` (`GODOT`, `DOTNET`, `SPINE_EXTENSION_DIR`);
  provide Godot 4.5.1 mono, .NET 9 and the official Spine 4.2 extension separately,
  plus Xvfb/Mesa on display-less Linux. Synthetic skeleton/atlas and generated artwork
  exercise real pixels, skin/state isolation, interruption and GC; no game files.
  Spine's [runtime license](https://esotericsoftware.com/spine-runtimes-license),
  not MIT, applies to the supplied native extension; do not commit its binaries.
- Known Waterfall Giant/Orobas backgrounds only bound excessive preprocessing of
  standard continuous ambient particles to one lifetime plus the original phase.
  Burst, one-shot, trail, sub-emitter, custom-material and long-lived effects remain
  unchanged. This mitigation is not evidence that reported crashes were OOM.
  Synthetic lifetime/cache/particle regressions: `tools/test-resource-safety.sh`.

Build locally from the parent repository after configuring `.env`:

```bash
../tools/android/build-port-mod.sh
```

Or build the schema-2 family compatibility pack from this submodule with local environment variables:

```bash
export DOTNET_BIN=/path/to/dotnet
export STS2_ORIGINAL_V1080_REFERENCE_DIR=/path/to/original-v0.108.0/bin/Debug
./tools/build-compat-matrix.sh --target v0.108.0

# Historical V1090 names identify the shared v0.109.x target; use the latest v0.109.1 gate.
export STS2_ORIGINAL_V1090_REFERENCE_DIR=/path/to/original-v0.109.1/bin/Debug
./tools/build-compat-matrix.sh --target v0.109.0

# Shared v0.110.x public-beta API/protocol target.
# Keep the historical V1100 variable/flavor name; point it at the latest v0.110.1 gate.
export STS2_ORIGINAL_V1100_REFERENCE_DIR=/path/to/original-v0.110.1/bin/Debug
./tools/build-compat-matrix.sh --target v0.110.0

# Current v0.111.0 public-beta API/handshake target.
export STS2_ORIGINAL_V1110_REFERENCE_DIR=/path/to/original-v0.111.0/bin/Debug
./tools/build-compat-matrix.sh --target v0.111.0
```

`tools/build-compat-pack.sh` is the legacy schema-1 path; use it only when also providing a matching `COMPAT_MANIFEST`.

The patched Godot runtime expects `STS2Mobile.dll` / `STS2Mobile.ModEntry`; the parent build script builds this skeleton under that assembly name and copies it into `android/assets/dotnet_bcl/`.
