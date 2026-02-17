# MapGenerationV1 ExecPlan

## Goals
- Generate a deterministic tile map with dense labyrinth shape using FBM simplex noise and band-pass thresholding.
- Produce only two tile states: `Ground` (walkable) and `Cliff` (blocked).
- Guarantee side entry gates that connect to the center-reachable region.
- Render play area on a Unity Tilemap and expose debug regeneration + bounds visualization.

## Non-Goals
- Runtime chunk streaming.
- ECS conversion/jobified generation pipeline.
- Zombie spawn/AI integration beyond map-side gates and spawn-ring bounds data.

## Steps
1. Add map runtime data model (`MapConfig`, `MapData`, `TileType`) with deterministic helpers.
2. Implement generator pipeline: FBM noise -> band-pass classification -> smoothing -> center opening -> gate carving.
3. Enforce connectivity with flood-fill culling and corridor carving for disconnected gates.
4. Add tilemap renderer and runtime map controller with debug regenerate + gizmos.
5. Document system behavior, perf constraints, and manual verification.

## Perf Risks
- Init-time cost scales with `O(width*height)` per pass; total passes include smoothing, flood-fill, and occasional distance-field rebuild per disconnected gate.
- Large maps can increase one-time allocation size (temporary arrays for walkable/reachable/queue/distance).
- Tilemap rebuild cost is paid on regenerate, not per-frame.

## Verification
- In Play Mode, confirm dense corridors and cliff-heavy layout.
- Confirm center area is always open.
- Confirm all four map sides have gates and gates connect to center-reachable region.
- Confirm only play area is rendered while spawn bounds remain debug-only.
