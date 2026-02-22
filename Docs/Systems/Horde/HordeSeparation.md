# HordeSeparationSystem

## Purpose
Prevent zombie overlap at high entity counts without Unity Physics by applying grid-based local separation after steering.

## Data
- `ZombieTag`, `LocalTransform` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeSeparationConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- Runtime system:
  - `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`

## Algorithm
Per frame:
1. Snapshot zombie entities and positions.
2. Build uniform spatial hash grid (`NativeParallelMultiHashMap<int,int>`) once from the initial snapshot.
3. Run `Iterations` Jacobi-style solver passes while reusing the same grid (grid is stale within the frame by design for performance).
4. For each zombie, inspect only 3x3 neighbor cells.
5. Early-cull neighbors outside `influenceRadius`.
6. Accumulate separation correction when neighbor distance is below `minDist = 2*radius`.
7. Exact-overlap pairs (`dist == 0`) use deterministic fallback normals so stacked entities can still separate.
8. Stop processing neighbors when `maxNeighbors` is reached (bounded worst-case).
9. Clamp correction by dt-normalized budget `min(maxPushPerFrame * dt, moveSpeed * dt)` (distributed per iteration) so total soft push cannot exceed unit speed budget.
10. Apply corrected positions.

Optional multi-pass is supported via `Iterations` (clamped to `1..8`).
`SeparationStrength` is clamped to `0..8` at runtime (not saturated to `0..1`).
Optional congestion fallback (default OFF): after iteration 0, the system can rebuild the grid once if a cheap congestion proxy indicates many entities hit the neighbor-cap.

## Default Tuning
- `Radius = 0.05`
- `minDist = 2 * Radius`
- `CellSizeFactor = 1.25` (`cellSize = minDist * factor`)
- `InfluenceRadiusFactor = 2.0` (`influence = minDist * factor`)
- `MaxNeighbors = 32`
- `SeparationStrength = 1.00`
- `MaxPushPerFrame = 2.20`
- `Iterations = 2`
- `RebuildGridWhenCongested = false` (disabled by default)
- `CongestionCapHitFractionThreshold = 0.10` (used only if fallback is enabled)

## Invariants
- Only zombies are moved by separation.
- Pressure config does not disable soft separation; pressure is augment-only.
- `MaxPushPerFrame` config is interpreted as units/second and converted to per-frame budget with `dt`.
- No per-frame managed allocations.
- No `O(N^2)` all-pairs scan.
- Soft displacement from separation is tied to each zombie's `ZombieMoveSpeed`.
- No runtime tuning/diagnostic log output from this system in gameplay.
- Update order: after `ZombieSteeringSystem` + `HordePressureFieldSystem`, before `HordeHardSeparationSystem`.

## Performance
- Burst jobs for snapshot, grid build, separation, and writeback.
- Complexity: `O(N * k)` where `k` is local neighbors in adjacent cells.
- Persistent native containers reused each frame.
- Spatial grid is built once per frame by default (significant CPU reduction when `Iterations > 1`).
- Optional congestion fallback may rebuild the grid one extra time after iteration 0 (still job-chained, no main-thread `Complete()`).

## Verification
1. Enter Play Mode with large zombie count (target 20k+).
2. Observe zombie packs spread without stacking to same point.
3. Profile and confirm `GC Alloc` remains `0 B` in gameplay.
4. Verify no job safety/race exceptions in Console.
