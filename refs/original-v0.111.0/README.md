# Original v0.111.0 reference placeholder

This directory is only a placeholder for the `original-v0.111.0`
ReferenceFlavor. Repository build scripts normally resolve the private original
PC reference directory from the parent project's `.env` / `local.properties`
configuration.

The reference directory must contain at least `sts2.dll`, `GodotSharp.dll`, and
`0Harmony.dll`. Do not commit those commercial/runtime binaries here.

```bash
STS2_ORIGINAL_V1110_REFERENCE_DIR=/path/to/v0.111.0/data_sts2_windows_x86_64
./tools/build-compat-matrix.sh --target v0.111.0
```
