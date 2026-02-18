# Flow Field Pathfinding (Plan A)

## Purpose
Precompute global navigation once per map regenerate, then steer each zombie in `O(1)` using a byte direction lookup.

## Data
- `MapRuntimeData`, `MapWalkableCell`, `GatePoint` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`, `Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)
- `FlowFieldSingleton` with `BlobAssetReference<FlowFieldBlob>` (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)
- `FlowFieldDirtyTag` to trigger rebuild once per map sync (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)

## Build Pipeline
`FlowFieldBuildSystem` (`Assets/_Project/Scripts/Map/FlowFieldBuildSystem.cs`) runs only when `FlowFieldDirtyTag` exists:
1. Collect center goal cells (walkable cells inside center-open radius).
2. Run 4-neighbor BFS integration field (`dist`).
3. Derive smooth direction by blending improving neighbors (8-neighbor gradient with corner-cutting).
4. Quantize normalized direction to 32 directions (`byte` index) and bake LUT in blob.
5. Bake `dir` + debug `dist` into persistent blob.
6. Replace old blob, remove dirty tag.

## Runtime Steering Contract
`ZombieSteeringSystem` consumes the blob:
- Outside map bounds: steer toward nearest gate world position.
- Inside map bounds: read `dir[cellIndex]` and fetch unit vector from blob LUT (`DirLut[dir]`).
- Fallback (`255` or invalid): seek center.

## Invariants
- No per-frame pathfinding work per entity.
- Runtime read pattern is fixed-size and cache-friendly.
- Structural changes happen only at map sync / flow rebuild phase.

## Performance Notes
- Build complexity: `O(width * height)` per regenerate.
- Runtime complexity: `O(zombies)` with constant work per zombie (+ small gate loop only when outside bounds).
- Direction field is byte-packed for memory bandwidth efficiency.
- Runtime steering remains one byte lookup + LUT fetch.

## Verification
1. Open `Assets/Scenes/SampleScene.unity`, enter Play Mode.
2. Regenerate map and confirm one flow build log line appears.
3. Spawn zombies outside map; confirm they pick a gate and enter.
4. Confirm in-map zombies move toward center without per-entity path recomputation.
5. Profile hot gameplay and verify `GC Alloc` remains `0 B` in play loop.
