# SparrowGH

A Grasshopper plugin that brings state-of-the-art 2D irregular nesting to Rhino, powered by the [Sparrow](https://github.com/JeroenGar/sparrow) engine.

Sparrow is a high-performance Rust library for 2D irregular strip packing — it consistently outperforms prior academic benchmarks. SparrowGH wraps it as a native Grasshopper component so you can nest any set of flat shapes directly inside your definition.

![demo](https://github.com/JeroenGar/sparrow/raw/main/data/demo.gif)

---

## Features

- **True irregular nesting** — works with any polygon shape, not just rectangles
- **Free or constrained rotation** — specify allowed angles or let Sparrow rotate freely
- **Minimum spacing** — set a kerf / toolpath gap between pieces
- **Non-blocking** — runs on a background thread; Grasshopper stays live while it works
- **Live progress** — ASCII progress bar + density/width updates via a Panel
- **Button or Toggle** — works with both; results persist after button resets
- **Rhino 7 & 8** — same plugin file works in both

---

## Installation

Download the folder for your platform from [`dist/`](dist/) and drop **all three files** into your Grasshopper Libraries folder:

| Platform | Libraries folder |
|---|---|
| Mac (Apple Silicon) | `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-...)/Libraries/` |
| Mac (Intel) | same path |
| Windows | `%APPDATA%\Grasshopper\Libraries\` |

Restart Rhino. A **Sparrow** tab will appear in Grasshopper.

> No Rust, no .NET SDK, nothing else to install. The compiled engine is included.

---

## Usage

Place a **Sparrow Nest** component (Sparrow tab → Nesting panel) and wire it up:

### Inputs

| Param | Nick | Description |
|---|---|---|
| Curves | C | Closed planar curves to nest. Projected to XY automatically. Any polygon shape. |
| StripHeight | H | Fixed height of the material strip (e.g. sheet width in mm). |
| Angles | A | Allowed rotation angles in degrees — e.g. `{0, 90, 180, 270}`. Leave empty for free rotation. |
| TimeSecs | T | How long to optimise in seconds (default 30). Longer = better packing. |
| Spacing | Sp | Minimum gap between pieces in model units. 0 = touching (default). |
| Run | R | Connect a **Button** or **Toggle**. Nesting fires on the rising edge. |

### Outputs

| Param | Nick | Description |
|---|---|---|
| NestedCurves | NC | Input curves repositioned in their nested layout. |
| Transforms | X | One Transform per curve — use to move geometry downstream. |
| StripWidth | W | Optimised strip length used. |
| Density | D | Packing density [0–1]. |
| Status | S | Live progress — connect a Panel to watch. Shows ASCII bar + phase + density. |

### Tips

- Connect the `S` output to a **Panel** to watch progress in real time
- Use a **Button** for one-shot runs; results stay visible after it resets
- Use a **Toggle** if you want nesting to re-run automatically when other inputs change
- Higher `TimeSecs` values give noticeably better packing density

---

## Building from source

### Prerequisites

- [Rust](https://rustup.rs/) (stable toolchain)
- [.NET SDK 8+](https://dot.net)
- Rhino 7 or 8 installed (for the Grasshopper SDK references)

### Build the engine

```bash
cd sparrow
cargo build --release
```

For cross-platform binaries:

```bash
rustup target add x86_64-apple-darwin
rustup target add x86_64-pc-windows-gnu
brew install mingw-w64   # macOS only, for Windows cross-compile

cargo build --release --target x86_64-apple-darwin
cargo build --release --target x86_64-pc-windows-gnu
```

### Build the plugin

```bash
cd SparrowGH
./build.sh
```

This compiles `SparrowGH.gha` and installs it (along with the engine binary) into your Grasshopper Libraries folder automatically.

---

## How it works

SparrowGH converts Grasshopper curves to Sparrow's JSON input format, launches the `sparrow` binary as a subprocess, streams its stdout for live progress updates, then parses the JSON output back into Rhino geometry (transforms + repositioned curves).

No FFI or DLL binding is needed — the JSON bridge keeps the integration simple and the engine fully replaceable.

---

## Credits

- Nesting engine: [Sparrow](https://github.com/JeroenGar/sparrow) by Jeroen Garrevoet
- Grasshopper bridge: SparrowGH

---

## License

The SparrowGH plugin code is MIT licensed. The Sparrow engine has its own license — see [`sparrow/LICENSE`](sparrow/LICENSE).
