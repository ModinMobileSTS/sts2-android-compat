# Original PC reference placeholders

This directory is an optional local placeholder for the v0.103.2 original PC
compile gate. Repository build scripts no longer rely on committed symlinks into
a developer workspace; they pass `CompatReferenceDir` from the parent project's
`.env` / `local.properties` configuration.

Preferred parent-repo setup:

```bash
cp .env.example .env
# edit STS2_ORIGINAL_V103_ROOT or STS2_ORIGINAL_V103_REFERENCE_DIR
REFERENCE_FLAVOR=original tools/android/build-port-mod.sh
```

Standalone `port-mod` setup:

```bash
export DOTNET_BIN=/path/to/dotnet
export STS2_ORIGINAL_V103_REFERENCE_DIR=/path/to/s21032/.godot/mono/temp/bin/Debug
"$DOTNET_BIN" build STS2AndroidPortCompat/STS2Mobile.csproj -p:ReferenceFlavor=original -v:q
```

The reference directory must contain `sts2.dll`, `GodotSharp.dll`, and
`0Harmony.dll`. Do not commit those game/runtime binaries.
