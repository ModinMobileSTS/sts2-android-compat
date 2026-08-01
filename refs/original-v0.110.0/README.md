# Original v0.110.x reference placeholder

This directory is only a placeholder for the historical `original-v0.110.0`
ReferenceFlavor. The shared target currently uses the latest verified v0.110.1
PC reference. Repository build scripts normally resolve the private original
PC reference directory from `.env` / `local.properties`.

The reference directory must contain at least `sts2.dll`, `GodotSharp.dll`, and
`0Harmony.dll`. Do not commit those commercial/runtime binaries here.

```bash
STS2_ORIGINAL_V1100_REFERENCE_DIR=/path/to/v0.110.1/data_sts2_windows_x86_64
./tools/build-compat-matrix.sh --target v0.110.0
```
