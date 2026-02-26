# HordePressureFieldSystem

## Purpose
Add a scalable congestion-avoidance pass using a density/pressure field on the expanded flow grid, so crowds disperse over time instead of jamming in chokepoints.

## Data
- `FlowFieldSingleton` (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)
- `WallFieldSingleton` (`Assets/_Project/Scripts/Map/WallFieldComponents.cs`) for wall distance/normal gating
- `ZombieTag`, `ZombieMoveSpeed`, `ZombieGoalIntent`, `ZombieVelocity`, `LocalTransform` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordePressureConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordePressureConfigAuthoring` (`Assets/_Project/Scripts/Horde/HordePressureConfigAuthoring.cs`)
  - MonoBehaviour + Baker for inspector-editable pressure config singleton values.
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
8. Applies anisotropic scaling to the pressure displacement before the final clamp:
   - parallel to desired movement (`PressureParallelScale`)
   - perpendicular to desired movement (`PressurePerpScale`)
9. When wall-near and locally dense, adds a wall-tangent drift to encourage lateral spread along long walls instead of queue-like compression:
   - deterministic per-entity lane sign (alternating by `EntityIndexInQuery`) breaks symmetry
   - optional flow/goal alignment only flips when `dot(tangent, alignDir) < -0.1` so near-zero ties keep the symmetry breaker
10. Combined pressure+tangent displacement is speed-capped using dt-normalized config budget: `min(MaxPushPerFrame * dt, moveSpeed * dt * SpeedFractionCap)`.
11. Resizes the singleton `PressureCell` buffer on the main thread only when cell count changes, before scheduling publish.
12. Publishes the active pressure grid to the resized buffer in a dedicated job (`BufferLookup<PressureCell>`), where the job writes values only (no length/capacity changes).
13. Rejects pressure move if resulting position lands in a blocked expanded flow cell.

## Update Order
- `ZombieSteeringSystem` writes `ZombieGoalIntent`.
- `HordeBackpressureSystem` applies goal intent/backpressure to velocity + base integration.
- `HordePressureFieldSystem` now runs **after** `HordeBackpressureSystem` so pressure/tangent displacement is not overwritten by backpressure integration.
- Runs before `HordeSeparationSystem`, which runs before `HordeHardSeparationSystem`.
- Runs before `WallRepulsionSystem`.
- `WallRepulsionSystem` remains the final blocked-cell safety correction.

## Invariants
- `HordePressureConfig` singleton is guaranteed at runtime:
  - preferred source: baked `HordePressureConfigAuthoring`
  - fallback: `HordePressureFieldSystem.OnCreate` creates one default singleton if missing
- No per-frame managed allocations in hot path.
- Pressure field is always aligned to the same expanded grid as flow steering.
- `MaxPushPerFrame` config is interpreted as units/second and converted to per-frame budget with `dt`.
- One-frame pressure config budget is `MaxPushPerFrame * dt`.
- One-frame pressure speed budget is `moveSpeed * dt * SpeedFractionCap`.
- Effective pressure cap per unit is `min(configBudget, speedBudget)`.
- Anisotropic pressure scaling is applied before the final cap, so the cap remains the last authority.
- Wall tangent drift is tangent-only and goal-signed; wall normal blocking/correction remains owned by `WallRepulsionSystem`.
- Backpressure tuning fields (consumed by `HordeBackpressureSystem`):
  - `BackpressureThreshold` (default `7.0`)
  - `BackpressureK` (default `0.20`)
  - `MinSpeedFactor` (default `0.30`)
  - `BackpressureMaxFactor` (default `1.0`)
- Pressure crowd-shape tuning fields:
  - `PressureParallelScale` (default `0.35`)
  - `PressurePerpScale` (default `1.25`)
  - `WallTangentStrength` (default `0.75`)
  - `WallTangentMaxPushPerFrame` (default `1.25`, dt-scaled internally)
  - `WallNearDistanceCells` (default `1.25`, wall-field cell distance)
  - `DenseUnitsPerCellThreshold` (default `5.0`)
  - `EnableWallTangentDriftDebug` (`0/1`, throttled log, debug builds only)
  - `DebugForceTangent` (`0/1`, debug-only proof mode for tangent application on first few zombies)
- Density accumulation is parallel and race-free without `unsafe` code by using per-thread bins plus a reduce pass.
- Pressure push is bounded and cannot exceed configured speed fraction per frame.
- Blocked-cell projection safety remains in wall repulsion, so units do not remain in blocked map cells.

## Performance
- Complexity:
  - field build: `O(N + G)` (`N` zombies, `G` expanded grid cells)
  - pressure apply: `O(N)` with constant-size neighborhood samples
- Uses persistent `NativeArray` buffers reused across frames.
- Uses a persistent per-thread debug counter array (only written when debug flag is enabled; no per-frame allocations).
- No structural entity changes and no manual sync points.
- No `Complete()` sync points in normal gameplay; publish copy stays scheduled/jobified.
- Optional debug logging is throttled (`120` frames) and only attempts to read the previous frame count when the job handle is already complete.
- Debug counters are tracked in a persistent per-thread buffer (eligible / pressureApplied / tangentApplied / densityValid / invalidWall / finalApplied).
- Debug magnitude sums are tracked in a persistent per-thread float buffer (`sumTangent`, `sumPressure`, `sumFinalDelta`) and reduced on the main thread only for throttled debug logging.
- Pressure publish lookup is created in `OnCreate` and updated per frame (`.Update(ref state)`), avoiding per-frame lookup creation overhead.

## Verification
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity` with large spawn counts.
2. Force dense groups into corridors/chokepoints and observe reduced long-term clumping.
3. Confirm zombies still converge toward center (single-point goal unchanged).
4. Confirm zombies are projected out if pushed into blocked map cells.
5. Place/keep a long wall and drive a dense horde along it:
   - units should spread laterally more
   - fewer ultra-tight single-file lines hugging the wall
   - movement remains smooth (no visible jitter increase)
6. Optional debug: set `EnableWallTangentDriftDebug = 1` and confirm a throttled log appears roughly every `120` frames:
   - `[HordePressure] eligible=E pressureApplied=P tangentApplied=T finalApplied=F avgTan=... avgPress=... avgDelta=... densityValid=D invalidWall=W frame=N`
7. If `eligible` remains `0`, set `DebugForceTangent = 1` to force tangent application for the first few zombies and verify `tangentApplied > 0`.
8. Open `Window > Entities > Hierarchy`, select `Default World`, and search for `HordePressureConfig` to verify the singleton exists.
9. Profiler watchlist:
   - `GC Alloc` stays `0 B`
   - no unexpected `Complete()` sync spikes
   - main thread not regressed versus pairwise-only baseline
