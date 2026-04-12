# SparrowGH — Developer Notes

## Building from source

### Requirements

- [Rust](https://rustup.rs/) stable toolchain
- [.NET SDK 8+](https://dot.net)
- Rhino 7 or 8 (for the Grasshopper SDK references)

### Engine

```bash
cd sparrow
cargo build --release
```

Cross-platform (from macOS):

```bash
rustup target add x86_64-apple-darwin
rustup target add x86_64-pc-windows-gnu
brew install mingw-w64  # for Windows cross-compile

cargo build --release --target x86_64-apple-darwin
cargo build --release --target x86_64-pc-windows-gnu
```

Packing modes:
```bash
# Strip packing (default)
./target/release/sparrow -i data/input/jakobs1.json -t 60

# Bin packing
./target/release/sparrow --mode bp -i data/input/test_bp.json -t 60
```

### Plugin

```bash
cd SparrowGH
./build.sh
```

Compiles `SparrowGH.gha` and installs it alongside the engine binary into your Grasshopper Libraries folder (Rhino 7 and 8).

---

## Architecture

- The `sparrow/` folder extends [JeroenGar/sparrow](https://github.com/JeroenGar/sparrow) (originally strip packing only) with a full bin packing mode (`--mode bp`). The bin packing optimizer lives in `src/bp_optimizer/` and is non-breaking — all original strip packing code is untouched.
- The GH plugin communicates with the engine via JSON files in the system temp directory. No FFI.
- Both components run the engine as a background subprocess and poll stdout for live progress updates.

## Input format (bin packing)

```json
{
  "name": "my_job",
  "bins": [{
    "id": 0, "stock": 20, "cost": 1,
    "shape": { "type": "rectangle", "data": { "x_min": 0, "y_min": 0, "width": 2500, "height": 1250 } }
  }],
  "items": [{
    "id": 0, "demand": 3,
    "allowed_orientations": [0, 90, 180, 270],
    "shape": { "type": "simple_polygon", "data": [[0,0],[100,0],[100,50],[0,50]] }
  }]
}
```

See `sparrow/data/input/test_bp.json` for a working example.
