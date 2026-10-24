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

Compile the current v0.111.0 target against its original PC references:

```bash
REFERENCE_FLAVOR=original-v0.111.0 tools/android/build-port-mod.sh
```

Compile against another configured reference set, for example the legacy v0.106.1 target:

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

Run the synthetic `Harmony.PatchAll()` early-UI-initialization regression without
commercial game code:

```bash
port-mod/tools/test-deferred-mod-patch-queue.sh
```

Run the synthetic extended-player treasure/rest-site regression, also without
commercial game code:

```bash
port-mod/tools/test-extended-multiplayer-rooms.sh
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
- `AndroidFontCoveragePatches` installs locale glyph coverage after normal
  localization initialization without depending on Android OEM system-font
  discovery. It preserves each control's base font and existing explicit
  fallbacks, then appends the matching STS2 `FontManager` Regular/Bold/Italic
  resource for the current locale. It also wraps independent `Window` theme
  fonts, including `PopupMenu` item/separator fonts and the popup owned by
  `OptionButton`; this covers dropdown rows that are not `Control` children.
  Existing controls receive one startup scan; later controls and popup windows
  use the single-node `SceneTree.NodeAdded` path. Do not replace this with a
  hot `Node.AddChild` recursive scan or package duplicate CJK fonts in the
  compatibility overlay.
- `DeferredModPatchQueue` covers the adjacent case where a user mod patches an
  STS2 Godot/UI type whose static fields directly read `LocManager.Instance` or
  other not-yet-essential state. During each user-mod initializer it guards both
  direct `PatchProcessor.Patch()` and the private per-target
  `PatchClassProcessor.ProcessPatchJob()` path used by `Harmony.PatchAll()`.
  Unsafe jobs retain the original Harmony processor/job and replay once after
  `LocManager`, `ModelDb`, model-id, and network-type initialization finish, so
  owner/order metadata, all patch kinds, and per-target prepare/cleanup behavior
  are not reconstructed or dropped. Safe targets in the same PatchAll class stay
  immediate. Model-type patches are intentionally not deferred, because many
  mods need their `ModelDb.Init` prefixes installed before model construction.
- `BaseLibCompatPatches` only prefix-disables
  `BaseLib.Utils.Patching.AsyncMethodCall.Create`, retaining the existing async
  state-machine hook workaround. `ExtendedSavePatches.Patch(Harmony)` is left
  intact so BaseLib can register its JSON metadata and persist CardModifier and
  other extended data. Already-lost save fields cannot be reconstructed.
  `tests/BaseLibExtendedSave.Tests` exercises real, user-supplied BaseLib and game
  assemblies in native Godot with separate disk-write/read processes; it does
  not certify Android Mono or complete mod initialization.
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
`lan_join_port`, `lan_multiplayer_save_player_id`, `max_multiplayer_enabled`,
and `max_multiplayer_players` from companion settings instead of adding
Android-only fields to the imported PC game assembly. Multiplayer run-save
canonicalization keeps a hidden stable `lan_multiplayer_save_player_id` so
changing the custom platform/player ID does not make `current_run_mp.save`
look like it belongs to another local player. `lan_multiplayer_enabled` is the
local multiplayer patch master switch;
when it is off, or when `sts2_lan_connect` / STS2 Game Lobby is already loaded,
the whole local LAN patch set is skipped to avoid protocol conflicts with that
MOD.

The LAN patch deliberately does not patch `MessageTypes.ToId`,
`MessageTypes.TryGetMessageType`, or `NetMessageBus.TryDeserializeMessage`.
The matching original game assembly remains the sole wire-protocol
implementation, including its per-version and MOD-aware message ordering, so an
Android peer uses the same codec as an unmodified PC peer. For v0.111.0 the
compat layer supplies `PeerVersionInfo.LocalDefault()` when constructing the
original host/client services, then leaves the original `HandshakeManager` in
control of version, ModelDb hash, and MOD validation. Protocol-affecting MODs
must still match on both peers.

Player capacities above four remain experimental, but
`Patches/ExtendedMultiplayerRoomPatches.cs` removes two deterministic vanilla
four-slot failures. Treasure rooms create one holder per synchronized relic,
bound the local default-focus index, and distribute award/fight hands by player
count; rest sites create one ordered character container per player before the
vanilla `_Ready()` loop indexes it. The patch does not alter treasure generation,
votes, award ownership, rest-site choices, or any multiplayer serialization.

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
The immediate-mode frame callback does not build tracking state. Managed modes
reuse owner/tip weak references and cache detail ancestry until the owner exits
the tree. A revealed hold only repeats the full reveal operation when its tip
changes; clearing/recreating tips retains the original hold deadline. Vanilla
tooltip following remains enabled.

`IntentAnimationPatches` binds private fields through Harmony once and reuses the
payload's animation-frame list when available. Older payloads lazily retain only
the current intent's played frames; animation changes and tree exit release that
cache. Combat-state updates outside `UpdateIntent` remain visible, with the same
24 FPS playback and bob tween. Input release handlers reject unrelated events
before settings lookups and cache reflection metadata per actual runtime type.
The reaction button owns global input routing only during an active press.

`TouchInputPatches` also refreshes the current cursor image/hotspot after an
Android mouse-targeting `Hidden` -> `Visible` transition in `NTargetingArrow`'s
`StopDrawing` or its own declared `_ExitTree`. This addresses Godot 4.5.1 Android
retaining the invisible pointer icon until another click. It invalidates only
the game's cursor-image cache and invokes its existing `UpdateCursor`; it does
not change mouse mode, native runtime, targeting behavior, or per-frame work.
Touch policy, controller ownership, explicit cursor hiding and still-hidden
outcomes are excluded. Older targets without their own exit callback do not
patch the inherited Godot method. `MobileHotPath.Tests` covers successful and
canceled targeting, scene exit, cursor overrides and these exclusion boundaries.

Synthetic regressions live in `tests/MobileHotPath.Tests` (relative to the compat
repository root). Supply `HarmonyReferenceDir` pointing to the packaged runtime
DLLs; run both the default shape and `-p:LegacyIntent=true`. They exercise actual
Harmony patches and behavioral transitions without commercial game code.

The in-game Android settings panel offers **High refresh (default)**,
**Request 60Hz**, and **Follow system**. It saves
`android_display_refresh_rate_mode=high/60hz/system`, then calls the launcher's
Java Activity bridge to update its request. `AndroidSettingsMerge` retains the
string through the original game's typed settings serialization. Legacy
`android_high_refresh_rate_enabled` booleans are launcher migration inputs only.
These choices request display policy, not an FPS/VSync change or a guaranteed
OEM display lock; unavailable 60Hz targets and follow-system clear prior votes.

Startup resource preparation uses the synchronous Godot-thread loading path
from before the background-loading change. Common/main-menu and learned/gameplay
warm-cache batches call `ResourceLoader.Load` and yield after every eight visited
items. VFX scenes load, instantiate and release synchronously, with a frame yield
between scenes and the existing frame waits for tree warmup. Cache reuse/ignore
semantics, scope, settings and protection rules are unchanged. A single load may
still block the frame. There is no shared background-request gate for startup;
the runtime asynchronous resource queues and shader-node optimization remain.

Shader compatibility uses enabled-only `SceneTree.NodeAdded` notifications and
one coalesced idle pass after parent/child Ready callbacks, instead of recursive
AddChild hooks. Disabling removes the subscription and pending references;
re-enabling seeds existing nodes. Per-node material isolation and the card-mask
shader exclusion remain intact. Existing replacements require restart to undo.

Runtime resource sessions additionally use `RuntimeAssetLoadingPatches`: guards
before dequeue share a cooperative 2ms frame budget with at most eight items per
phase. Repeated processing in the same frame cannot renew the quota. The game
still owns completion, errors, its existing in-flight limit and serial VFX loads.
Deferred queues remain intact; loading may take longer, but never falsely finish.

`CombatVfxPoolPatches` keeps an explicit whitelist of completed stock damage
numbers, hit sparks, shivs, big slashes and fire bursts within one combat room,
with idle limits 16/8/8/2/2. Overflow still creates complete effects. The two new
families use known particle-only trees and Task/CTS playback; no generic node
snapshot expansion, VFX enumeration, card/creature/Spine state or startup-warmup
spare retention is added. Original factories, Ready, randomization, screen shake
and playback timing still execute. Immutable async leases prevent stale release;
external removal keeps original destruction, and room exit frees idle instances.
Re-rent restores transforms/colors/visibility, then Ready restarts particles and
creates a new CTS. Shiv tint materials remain instance-local; slash/fire retain
their original per-node SelfModulate tint. Unknown child scripts and foreign
Harmony factory/lifecycle/tint patches opt out. Native regressions cover the new
families' reset/replay, stale/cancelled leases, two-slot overflow and opt-outs;
read-only original-DLL checks include their factory/playback/reset contracts.
Effect counts, gameplay/network, preload scope and GC settings are unchanged;
first-use resource loading and shader compilation may still stall.

`AndroidFontSizeScaler` caches fixed metadata keys, leaves untouched 100% fonts
inherited, and avoids equal-value setters/auto-size adjustments. Scaling and
restoration use original base sizes without compounding on reentry; locale font
fallback remains unchanged.

`tests/FramePreparation.Tests` runs the production shader, queue, effect-pool and
font-scaling code with the official Godot 4.5.1 .NET engine and packaged Harmony.
Synthetic fixtures cover runtime resource failures, stale playback, bounded
retention, cancellation, material isolation and font restoration. The removed
startup background loader no longer has single-request/retrieval tests.
Build with `HarmonyReferenceDir`, then
launch Godot with `--headless --path tests/FramePreparation.Tests`; on Linux set
`DOTNET_ROOT` to the .NET SDK root and `LD_PRELOAD=libgcc_s.so.1` for MonoMod's
native unwinder. The malformed-resource error is expected before the final PASS.
Optionally set `STS2_FRAME_REFERENCE_DLLS` to semicolon-separated local original
DLL paths for read-only Cecil checks of queue IL and VFX factory/playback shapes;
the harness never executes those game assemblies or includes commercial data.

`Patches/MobileInputPolicyPatches.cs` switches the touch affordances according to
real input activity, not connected devices. Hardware key-down, mouse clicks,
wheel ticks or deliberate movement restore PC interaction; a new finger press
restores the user's saved touch settings. An active pointer gesture retains its
policy through the complete GUI release. Competing pointer streams and their
residual releases cannot take over that gesture or click a second time.
The Android `GodotInputSourceHost` signal is ordered before native dispatch;
the receiver flushes the preceding source before updating provenance. Virtual
keyboard/IME events do not switch policy, and touch-emulated or floating-button
mouse events remain touch. No setting, polling or renderer change is introduced.

`Patches/MobileTapPreviewPatches.cs` pins a playable hand card when the current
policy is touch and companion `touch_lift_preview` is true; a second tap follows
`touch_lift_retap_action` (`put_down`, `play`, or `none`). Reward/shop confirmation
and tooltip restrictions use the same policy. Switching clears stale selections
and timers without playing cards, granting rewards or purchasing an item.
`tests/MobileInputPolicy.Tests` covers ownership and deferred transitions;
`tests/MobileHotPath.Tests` exercises all five production consumers with packaged
Harmony, in both default and `-p:LegacyIntent=true` shapes.

`Patches/AndroidInputCompatPatches.cs` maps Android Back to game cancel/pause,
while physical Escape retains the game's real, rebindable press/release path.
It also emits touch-marked right-clicks for two-finger inspect when
`mobile_two_finger_inspect` is enabled and normalizes trigger-axis controller
input on Android. Automatic touch/keyboard-mouse policy requires the current
APK and full compat; offline bootstrap does not contain these interaction patches.
