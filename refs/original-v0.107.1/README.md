# Original v0.107.1 reference placeholders

This directory is an optional local placeholder for the v0.107.1 stable original PC/reference DLLs. Repository build scripts normally resolve the reference directory from `.env` / `local.properties` instead of requiring files here.

Required files for the compile gate:

- `sts2.dll`
- `GodotSharp.dll`
- `0Harmony.dll`

Recommended parent-repository configuration:

```bash
STS2_ORIGINAL_V1071_ROOT=/path/to/sts2-original-v0.107.1
# or
STS2_ORIGINAL_V1071_REFERENCE_DIR=/path/to/sts2-original-v0.107.1/.godot/mono/temp/bin/Debug
REFERENCE_FLAVOR=original-v0.107.1 tools/android/build-port-mod.sh
```

Standalone submodule build:

```bash
REFERENCE_FLAVOR=original-v0.107.1 ./tools/build-compat-pack.sh
```
