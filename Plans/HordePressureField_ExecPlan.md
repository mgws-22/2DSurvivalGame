# Horde Pressure Field ExecPlan

## Goals
- Add a density/pressure steering field that scales better than pairwise-only separation at high counts.
- Build the field on the same expanded grid used by `FlowFieldBlob` (map + `spawnMargin`).
- Keep hot paths allocation-free, Burst/jobified, and preserve blocked-tile end-of-frame safety via wall projection.

## Non-goals
- Replacing flow-field center targeting (goal remains single center point).
- Refactoring map generation or flow build algorithms.
- Adding Unity Physics colliders/rigidbodies.

## Implementation steps
1. Add `HordePressureConfig` to ECS components with conservative defaults and speed-capped push settings.
2. Add `HordePressureFieldSystem`:
   - persistent native arrays for density + pressure buffers
   - rebuild density/pressure on configurable frame interval
   - apply per-zombie pressure push in Burst jobs, capped by speed/frame limits
   - keep update order between steering and wall safety systems
3. Gate legacy soft pairwise separation behind config flag (`DisablePairwiseSeparationWhenPressureEnabled`) so pressure can replace or augment as needed.
4. Update docs: system doc, architecture index, dev log.

## Perf risks
- Density accumulation can become a bottleneck if done too frequently at very high counts.
- Full-grid smoothing cost is proportional to expanded grid size.

## Mitigations
- Keep all loops in Burst jobs and persistent containers.
- Support configurable field update interval.
- Keep blur passes low (`0..2`) and bounded.

## Verification
1. Spawn dense crowds (target 20k+) in corridor/choke maps.
2. Confirm congestion disperses over time instead of long-term clumping.
3. Confirm zombies do not remain in blocked map cells at frame end.
4. Profiler watchlist: `GC Alloc = 0 B`, no unexpected sync points, stable main-thread time.
