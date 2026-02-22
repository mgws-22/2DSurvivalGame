# HordeHardSeparationSystem

## Purpose
Provide a stronger overlap solver (Jacobi/PBD-style) for zombies using an ECS spatial hash and iterative correction passes.

Default behavior is jam-gated by default:
- `Enabled = 1`
- `JamOnly = 1`
- hard solve applies only to units classified as jammed.

## Data
- `ZombieTag`, `LocalTransform` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeHardSeparationConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeHardSeparationConfigAuthoring` (`Assets/_Project/Scripts/Horde/HordeHardSeparationConfigAuthoring.cs`)
- Runtime system:
  - `Assets/_Project/Scripts/Horde/HordeHardSeparationSystem.cs`

## Algorithm
Per frame when enabled:
1. Snapshot zombie entities, positions, and move speeds.
2. Copy pressure field buffer into persistent snapshot array.
3. Build per-unit jam mask:
   - `jamThr = JamPressureThreshold` (fallback to pressure backpressure threshold when `<= 0`)
   - `denseThr = DensePressureThreshold` (fallback to `jamThr` when `<= 0`, preserving old behavior)
   - hard jam if `localPressure > jamThr` OR (`localPressure > denseThr` AND `slow`)
   - `slow` uses sampled displacement speed between hard-system ticks and `SlowSpeedFraction` (default `0.2`)
4. Build a spatial hash grid (`NativeParallelMultiHashMap<int,int>`).
   - per-iteration clear is dependency-chained in jobs (no main-thread sync).
5. For each zombie, scan 3x3 neighbor cells.
6. Process up to `MaxNeighbors` (or `MaxNeighborsJam` in jam mode) candidates.
7. For penetrations under `2*Radius`, accumulate correction in `delta[i]` only.
8. Handle `dist ~= 0` deterministically from `(index, iteration)` hash.
9. Clamp each zombie correction to max correction cap (`MaxCorrectionPerIter` or `MaxPushPerFrameJam` in jam mode).
10. Apply `posWrite[i] = posRead[i] + delta[i]`.
11. Swap buffers and repeat for configured iterations (`Iterations` or `IterationsJam`).
12. Write final positions back to `LocalTransform`.
13. Store sampled positions for next-frame slow-speed jam check.
## Invariants
- No Unity Physics/collider dependency.
- Compute pass is race-free: each worker writes only `delta[i]`.
- No per-frame managed allocations in hot path.
- Neighbor work is bounded by `MaxNeighbors`.
- Grid capacity is pre-sized from entity count with slack before build to avoid runtime overflow.
- If `JamOnly = 1`, non-jam units always write zero hard-separation delta.
- `DensePressureThreshold <= 0` preserves previous gating semantics by falling back to `JamPressureThreshold`.
- Update order: after `ZombieSteeringSystem` + `HordePressureFieldSystem` + `HordeSeparationSystem`, before `WallRepulsionSystem`.

## Performance
- Burst jobs for snapshot, grid build, delta solve, apply, and writeback.
- Spatial hash keeps neighbor checks local.
- Complexity: `O(N * MaxNeighbors * Iterations)`.
- Uses persistent native containers to avoid GC pressure.

## Verification
1. Add `HordeHardSeparationConfigAuthoring` to a scene object.
2. Keep defaults (`Enabled=1`, `JamOnly=1`) and verify open-flow windows stay close to previous behavior.
3. In jam windows, verify overlap drops faster than with soft-only separation.
4. Optionally set `Enabled=0` to compare against baseline behavior.
5. Profile:
   - `GC Alloc` remains `0 B` in gameplay.
   - no job safety/race exceptions.
   - no unexpected main-thread spikes from the solver.
