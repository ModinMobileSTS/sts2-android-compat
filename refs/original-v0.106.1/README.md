# Original v0.106.1 reference placeholders

This directory is an optional local placeholder for the beta v0.106.1 original
PC compile gate. Repository build scripts no longer rely on committed symlinks
into a developer workspace; they pass `CompatReferenceDir` from the parent
project's `.env` / `local.properties` configuration.

Preferred parent-repo setup:

```bash
cp .env.example .env
# edit STS2_ORIGINAL_V1061_ROOT or STS2_ORIGINAL_V1061_REFERENCE_DIR
REFERENCE_FLAVOR=original-v0.106.1 tools/android/build-port-mod.sh
```

Standalone `port-mod` setup:

```bash
export DOTNET_BIN=/path/to/dotnet
export STS2_ORIGINAL_V1061_REFERENCE_DIR=/path/to/s201061/.godot/mono/temp/bin/Debug
"$DOTNET_BIN" build STS2AndroidPortCompat/STS2Mobile.csproj -p:ReferenceFlavor=original-v0.106.1 -v:q
```

The reference directory must contain `sts2.dll`, `GodotSharp.dll`, and
`0Harmony.dll`. Do not commit those game/runtime binaries.
