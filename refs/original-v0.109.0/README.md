# Original v0.109.x reference placeholders

This directory and the `original-v0.109.0` ReferenceFlavor keep their historical
names because the shared target id remains `v0.109.0` for existing launch
profiles. The target supports both v0.109.0 and v0.109.1; new builds should use
the latest verified v0.109.1 original PC/reference DLLs. Repository build
scripts normally resolve the reference directory from `.env` /
`local.properties`.

The reference directory must contain at least `sts2.dll`, `GodotSharp.dll`, and
`0Harmony.dll`. Do not commit those commercial/runtime binaries here.

```bash
STS2_ORIGINAL_V1090_REFERENCE_DIR=/path/to/v0.109.1/data_sts2_windows_x86_64
./tools/build-compat-matrix.sh --target v0.109.0
```
