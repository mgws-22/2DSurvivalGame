# HordePressureFieldSystem

## Purpose
Add a scalable congestion-avoidance pass using a density/pressure field on the expanded flow grid, so crowds disperse over time instead of jamming in chokepoints.

## Data
- `FlowFieldSingleton` (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)
- `ZombieTag`, `ZombieMoveSpeed`, `LocalTransform` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordePressureConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- Runtime system:
  - `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`

## Runtime Logic
1. Uses `FlowFieldBlob` dimensions/origin/cell size (expanded map + spawn margin) as the pressure grid.
2. Rebuilds density at configurable interval (`FieldUpdateIntervalFrames`):
   - clear persistent per-thread density bins (`cellCount * workerCount`)
   - accumulate units in parallel where each worker writes only to its own bin slice (`threadIndex * cellCount + cellIndex`)
   - reduce per-thread bins into final density array (`sum worker slices per cell`)
3. Converts density to pressure (`max(0, density - TargetUnitsPerCell)`), with blocked expanded cells assigned a high penalty.
4. Optional bounded blur passes (`0..2`) smooth pressure to reduce jitter and improve corridor behavior.
5. Per zombie, samples local pressure gradient and pushes toward lower pressure.
6. Adds deterministic per-entity spread bias in tie/symmetry cases so dense stacks do not move in perfect lockstep.
7. Removes any backward component vs flow direction (`dot(pressureDir, flowDir) < 0` projected out).
8. Push is speed-capped using dt-normalized config budget: `min(MaxPushPerFrame * dt, moveSpeed * dt * SpeedFractionCap)`.
9. Resizes the singleton `PressureCell` buffer on the main thread only when cell count changes, before scheduling publish.
10. Publishes the active pressure grid to the resized buffer in a dedicated job (`BufferLookup<PressureCell>`), where the job writes values only (no length/capacity changes).
11. Rejects pressure move if resulting position lands in a blocked expanded flow cell.

## Update Order
- Runs after `ZombieSteeringSystem` (which now writes `ZombieGoalIntent` only).
- Runs before `HordeBackpressureSystem` (which reads published `PressureCell` and applies scaled goal intent).
- Runs before `HordeSeparationSystem`, which runs before `HordeHardSeparationSystem`.
- Runs before `WallRepulsionSystem`.
- `WallRepulsionSystem` remains the final blocked-cell safety correction.

## Invariants
- No per-frame managed allocations in hot path.
- Pressure field is always aligned to the same expanded grid as flow steering.
- `MaxPushPerFrame` config is interpreted as units/second and converted to per-frame budget with `dt`.
- One-frame pressure config budget is `MaxPushPerFrame * dt`.
- One-frame pressure speed budget is `moveSpeed * dt * SpeedFractionCap`.
- Effective pressure cap per unit is `min(configBudget, speedBudget)`.
- Backpressure tuning fields (consumed by `HordeBackpressureSystem` and metrics):
  - `BackpressureThreshold` (default `5.5`)
  - `BackpressureK` (default `0.15`)
  - `MinSpeedFactor` (default `0.30`)
  - `BackpressureMaxFactor` (default `1.0`)
- Density accumulation is parallel and race-free without `unsafe` code by using per-thread bins plus a reduce pass.
- Pressure push is bounded and cannot exceed configured speed fraction per frame.
- Blocked-cell projection safety remains in wall repulsion, so units do not remain in blocked map cells.

## Performance
- Complexity:
  - field build: `O(N + G)` (`N` zombies, `G` expanded grid cells)
  - pressure apply: `O(N)` with constant-size neighborhood samples
- Uses persistent `NativeArray` buffers reused across frames.
- No structural entity changes and no manual sync points.
- No `Complete()` sync points; publish copy stays scheduled/jobified.
- Pressure publish lookup is created in `OnCreate` and updated per frame (`.Update(ref state)`), avoiding per-frame lookup creation overhead.

## Verification
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity` with large spawn counts.
2. Force dense groups into corridors/chokepoints and observe reduced long-term clumping.
3. Confirm zombies still converge toward center (single-point goal unchanged).
4. Confirm zombies are projected out if pushed into blocked map cells.
5. Profiler watchlist:
   - `GC Alloc` stays `0 B`
   - no unexpected `Complete()` sync spikes
   - main thread not regressed versus pairwise-only baseline
