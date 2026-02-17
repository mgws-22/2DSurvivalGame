# Map Generator (v1)

## Purpose
`MapGenerator` builds a deterministic 2D grid for the play area with two tile states:
- `Ground`: walkable
- `Cliff`: blocked

Target shape is a dense labyrinth: mostly cliffs, narrow winding corridors.

## Runtime Pieces
- `MapConfig` (`Assets/_Project/Scripts/Map/MapConfig.cs`)
  - Generation parameters: dimensions, seed, tile size, FBM noise settings, domain warp, band-pass width, smoothing passes, center opening, gate settings, spawn margin.
- `MapData` (`Assets/_Project/Scripts/Map/MapData.cs`)
  - Stores tile classification (`TileType`) and walkability bool per cell.
  - Helpers: `IsInMap`, `GridToWorld`, play/spawn bounds.
  - Stores gate centers for side entry points.
- `MapGenerator` (`Assets/_Project/Scripts/Map/MapGenerator.cs`)
  - Deterministic map generation pipeline.
- `MapTilemapRenderer` (`Assets/_Project/Scripts/Map/MapTilemapRenderer.cs`)
  - Renders play area only to a Tilemap.
- `MapGenerationController` (`Assets/_Project/Scripts/Map/MapGenerationController.cs`)
  - Runtime bootstrap, generation trigger, gizmos, context-menu regenerate.

## Generation Pipeline
1. **Noise field (deterministic)**
- Uses `Unity.Mathematics.noise.snoise` FBM.
- Optional domain warp offsets sample positions before FBM.
- Seed is hashed into deterministic RNG streams.

2. **Band-pass tile classification**
- `Ground` if `abs(noiseValue) < bandWidth`
- else `Cliff`

This creates stripe/isoline-style corridors suitable for labyrinth layouts.

3. **Smoothing pass (1-2 typical)**
- Removes isolated speckles and tiny artifacts.
- Ground survives if enough neighboring ground remains.
- Cliff can become ground when heavily surrounded by ground.

4. **Center opening**
- Circular region at map center (`centerOpenRadius`) is forced to `Ground`.
- Guarantees a reachable play core.

5. **Side gates**
- Creates `gateCountPerSide` gate centers on each map side (top/bottom/left/right), with deterministic jitter.
- Carves each gate as a radius disk (`gateRadius`) to `Ground`.

6. **Connectivity enforcement**
- Flood-fill from center over walkable cells.
- Convert unreachable ground to cliffs.
- For each disconnected gate region:
  - Build distance field from current reachable region.
  - Carve a corridor via a biased random walk toward decreasing distance.
  - Re-run flood-fill.
- Final unreachable cleanup ensures final ground set is center-connected.

## Spawn Ring
- `spawnMargin` expands bounds beyond the play area for spawn systems.
- Spawn ring is represented in `MapData` bounds and gizmos only.
- Tilemap renderer still renders play area only (`width x height`).

## Invariants
- Deterministic for identical `MapConfig` + world origin.
- Center-open area is always ground.
- Gates exist on each side when `gateCountPerSide > 0`.
- Final walkable cells are center-reachable.

## Performance Notes
- Generation is init/regenerate-time only; no per-frame allocation from this module.
- Main complexity is linear per pass: `O(width*height)`.
- Additional gate-corridor work is bounded by gate count and map area.
- Tilemap rebuild uses `SetTilesBlock` in one batch per regenerate.

## Verification (Unity)
1. Enter Play Mode in `SampleScene`.
2. Locate auto-created `Map Generation` object if none is placed.
3. Confirm rendered tilemap is cliff-heavy with narrow ground corridors.
4. Confirm center region is open ground.
5. Enable gizmos on selected `Map Generation` object:
- green wireframe = play bounds
- yellow wireframe = spawn bounds (outside play area)
- cyan circles = gates on all sides
6. Use component context menu `Regenerate Map` and verify deterministic output for fixed seed.
