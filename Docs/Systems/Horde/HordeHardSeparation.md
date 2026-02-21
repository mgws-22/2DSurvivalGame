# HordeHardSeparationSystem

## Purpose
Provide a stronger overlap solver (Jacobi/PBD-style) for zombies using an ECS spatial hash and iterative correction passes.

Default behavior is unchanged because the config is explicitly disabled by default (`Enabled = 0`).

## Data
- `ZombieTag`, `LocalTransform` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeHardSeparationConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeHardSeparationConfigAuthoring` (`Assets/_Project/Scripts/Horde/HordeHardSeparationConfigAuthoring.cs`)
- Runtime system:
  - `Assets/_Project/Scripts/Horde/HordeHardSeparationSystem.cs`

## Algorithm
Per frame when enabled:
1. Snapshot zombie positions into `posRead`.
2. Build a spatial hash grid (`NativeParallelMultiHashMap<int,int>`).
3. For each zombie, scan 3x3 neighbor cells.
4. Process up to `MaxNeighbors` candidates.
5. For penetrations under `2*Radius`, accumulate correction in `delta[i]` only.
6. Handle `dist ~= 0` deterministically from `(index, iteration)` hash.
7. Clamp each zombie correction to `MaxCorrectionPerIter`.
8. Apply `posWrite[i] = posRead[i] + delta[i]`.
9. Swap buffers and repeat for `Iterations` (clamped to `1..2`).
10. Write final positions back to `LocalTransform`.

## Invariants
- No Unity Physics/collider dependency.
- Compute pass is race-free: each worker writes only `delta[i]`.
- No per-frame managed allocations in hot path.
- Neighbor work is bounded by `MaxNeighbors`.
- Update order: after `ZombieSteeringSystem` + `HordePressureFieldSystem` + `HordeSeparationSystem`, before `WallRepulsionSystem`.

## Performance
- Burst jobs for snapshot, grid build, delta solve, apply, and writeback.
- Spatial hash keeps neighbor checks local.
- Complexity: `O(N * MaxNeighbors * Iterations)`.
- Uses persistent native containers to avoid GC pressure.

## Verification
1. Add `HordeHardSeparationConfigAuthoring` to a scene object.
2. Leave `Enabled` off and verify behavior is unchanged.
3. Set `Enabled` on and observe crowd overlap reduces significantly.
4. Profile:
   - `GC Alloc` remains `0 B` in gameplay.
   - no job safety/race exceptions.
   - no unexpected main-thread spikes from the solver.
