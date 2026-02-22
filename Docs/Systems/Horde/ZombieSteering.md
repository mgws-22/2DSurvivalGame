# ZombieSteeringSystem + HordeBackpressureSystem

## Purpose
Split goal steering into two jobified steps:
1. `ZombieSteeringSystem` computes flow/center intent only.
2. `HordeBackpressureSystem` scales that intent from the pressure field and applies movement.

This keeps backpressure scoped to goal/flow intent only; separation and wall repulsion stay unscaled.

## Data
- `ZombieTag`, `ZombieMoveSpeed`, `ZombieVelocity`, `ZombieSteeringState`, `ZombieGoalIntent` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `LocalTransform` (Unity.Transforms)
- `MapRuntimeData` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`)
- `FlowFieldSingleton` (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)
- `HordePressureConfig`, `ZombieAccelerationConfig`, `PressureFieldBufferTag`, `PressureCell` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)

## Runtime Logic
1. `ZombieSteeringSystem`:
   - resolves flow direction (or center fallback),
   - validates next-step walkability,
   - writes `ZombieGoalIntent { Direction, StepDistance }`,
   - does not move `LocalTransform`.
2. `HordeBackpressureSystem` (after pressure publish):
   - samples `PressureCell` at the unit's current flow cell,
   - computes scale:
     - `excess = max(0, pressure - BackpressureThreshold)`
     - `raw = 1 / (1 + BackpressureK * excess)`
     - `speedScale = clamp(raw, MinSpeedFactor, BackpressureMaxFactor)`
   - computes `vDesired = normalized(goalIntent.Direction) * (moveSpeed * speedScale)`,
   - applies acceleration-limited velocity update:
     - `dv = vDesired - vCurrent`
     - `maxDv = MaxAccel * dt` (or `MaxAccel * DecelMultiplier * dt` when slowing)
     - clamp `dv` by magnitude, then `vNext = vCurrent + dv`
   - integrates `LocalTransform += vNext * dt`.

Acceleration and backpressure are composited in that order: backpressure first reduces desired speed in dense cells, then acceleration/deceleration limits how fast units can chase that desired speed. This prevents instant velocity jumps while preserving pressure intent.

Separation/hard-separation/wall systems still run after movement and can apply corrective position offsets without scaling their authority by backpressure. The velocity state represents command/integration velocity, while collision-resolution systems remain independent safety/crowd-correction passes.

## Update Order
- `ZombieSteeringSystem` runs before `HordePressureFieldSystem`.
- `HordeBackpressureSystem` runs after `HordePressureFieldSystem`.
- `HordeBackpressureSystem` runs before separation/hard-separation/wall systems.

## Invariants
- Goal-intent is separated from integration and can be scaled independently.
- Backpressure is pressure-field driven only.
- Separation and wall repulsion are unaffected by backpressure scale and acceleration config.
- `ZombieVelocity` is persistent per-unit state and is initialized to zero on baked prefabs.
- No main-thread pressure buffer reads.

## Performance
- Both systems are Burst `IJobEntity` and allocation-free.
- No structural changes per frame.
- No explicit sync points (`Complete()`).

## Verification
1. Enter Play Mode with pressure enabled.
2. Verify no `InvalidOperationException`/Burst buffer restriction exceptions.
3. In Editor/Development builds, confirm one startup log exists: `[HordeAccel] cfg enabled=... timeToMax=... maxAccel=... decelMultiplier=...`.
4. In open space, confirm speed remains near unscaled baseline.
5. In chokepoints, confirm only congested groups slow via backpressure while separation/wall still resolve overlap/collision.
