# Original v0.109.0 reference placeholders

This directory is an optional local placeholder for the v0.109.0 public beta
original PC/reference DLLs. Repository build scripts normally resolve the
reference directory from `.env` / `local.properties`.

The reference directory must contain at least `sts2.dll`, `GodotSharp.dll`, and
`0Harmony.dll`. Do not commit those commercial/runtime binaries here.

```bash
STS2_ORIGINAL_V1090_REFERENCE_DIR=/path/to/v0.109.0/data_sts2_windows_x86_64
./tools/build-compat-matrix.sh --target v0.109.0
```
