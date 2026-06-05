# Original v0.107.0 reference placeholders

This directory is an optional local placeholder for the beta v0.107.0 original PC/reference DLLs. Repository build scripts normally resolve the reference directory from `.env` / `local.properties` instead of requiring files here.

Required files for the compile gate:

- `sts2.dll`
- `GodotSharp.dll`
- `0Harmony.dll`

Recommended parent-repository configuration:

```bash
STS2_ORIGINAL_V1070_ROOT=/path/to/sts2-original-v0.107.0
# or
STS2_ORIGINAL_V1070_REFERENCE_DIR=/path/to/sts2-original-v0.107.0/.godot/mono/temp/bin/Debug
REFERENCE_FLAVOR=original-v0.107.0 tools/android/build-port-mod.sh
```

Standalone submodule build:

```bash
REFERENCE_FLAVOR=original-v0.107.0 ./tools/build-compat-pack.sh
```
