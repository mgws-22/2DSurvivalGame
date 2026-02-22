# CameraWasdZoomController

## Purpose
Provide simple RTS-style camera controls in gameplay:
- pan with `WASD` (and arrow keys)
- orthographic zoom with mouse wheel toward current mouse position.

## Data
- Runtime script:
  - `Assets/_Project/Scripts/CameraWasdZoomController.cs`
- Scene binding:
  - `Assets/Scenes/SampleScene.unity` on `Main Camera`.

## Runtime Logic
1. Read movement input each frame from `WASD`/arrow keys.
2. Normalize diagonal movement to keep pan speed consistent.
3. Apply pan on XY plane, preserving camera Z.
4. On mouse wheel input, compute world point under cursor on configurable focus plane (`_focusPlaneZ`).
5. Change orthographic size with clamped limits.
6. Offset camera position so the same world point stays under the cursor after zoom.

## Invariants
- No per-frame managed allocations.
- Works only for orthographic zoom behavior (script safely early-outs for non-orthographic cameras).
- Camera movement is XY-only; Z position is unchanged by pan/zoom correction.

## Verification
1. Enter Play Mode in `SampleScene`.
2. Hold `W`, `A`, `S`, `D` and verify camera pans.
3. Scroll mouse wheel while cursor is over a map point and verify zoom centers on that cursor location.
4. Verify `GC Alloc` remains `0 B` while moving/zooming camera.
