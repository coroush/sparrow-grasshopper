# SparrowGH

A Grasshopper plugin for 2D irregular nesting, built on top of [Sparrow](https://github.com/JeroenGar/sparrow) — a state-of-the-art Rust-based solver for 2D irregular packing problems.

This fork extends the original Sparrow engine with **bin packing** (nesting onto multiple fixed-size sheets) alongside the original **strip packing** mode. Both are exposed as Grasshopper components.


## Installation

Download the folder matching your platform from [`dist/`](dist/) and copy all three files into your Grasshopper Libraries folder:

Restart Rhino. A **Sparrow** tab will appear in Grasshopper with two components.


## Components

Both components live in the **Sparrow → Nesting** panel.


### Sparrow Nest  `SpNest`

Nests closed planar curves onto one or more fixed-size rectangular sheets. Outputs a DataTree of nested curves, indices and transforms per sheet.

![srtip-rh](imgs/multi-sheet-rh.png)
![strip-gh](imgs/multi-sheet-gh.png)



### Sparrow Strip Nest  `SpStrip`

Nests closed planar curves into a strip of fixed height and variable width. Returns a flat list of nested curves.

![srtip-rh](imgs/strip-rh.png)
![strip-gh](imgs/strip-gh.png)



## Notes

- Disable a running component to kill the engine process immediately.
- Results are cached — the last successful run is shown until you press Run again.
- The engine communicates via JSON in the system temp directory. no FFI, no network.
- For build instructions and input format details see [`README_dev.md`](README_dev.md).


## License

Plugin code: MIT.  
Engine: see [`sparrow/LICENSE`](sparrow/LICENSE).
