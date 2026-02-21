# HordeTuningQuickMetricsSystem

## Purpose
Provide low-overhead runtime tuning metrics (sampled overlap and jam rates) so crowd steering/separation tuning can be driven by data during Play Mode.

## Data
- `ZombieTag`, `ZombieMoveSpeed`, `LocalTransform` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeSeparationConfig`, `HordePressureConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `HordeTuningQuickConfig`, `HordeTuningQuickMetrics` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- Runtime system:
  - `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`

## Runtime Logic
1. Runs after `WallRepulsionSystem` so metrics reflect final resolved positions.
2. Every `LogEveryNFrames`, snapshots zombie entities/positions/speeds.
3. Builds a spatial hash grid and evaluates only sampled zombies (`EntityIndexInQuery % SampleStride == 0`).
4. `overlap(sample)%`:
   - sampled zombie counts as overlap if any nearby neighbor is closer than `2 * Radius`.
5. `jam%` (cheap approximation):
   - sampled zombie is jammed if:
     - estimated local density (`1 + nearby neighbors within influence radius`) is `>= TargetUnitsPerCell`, and
     - sampled displacement speed between metric ticks is below `moveSpeed * 0.2`.
6. Logs one `[HordeTune]` line per metrics tick in Editor/Development builds.

## Invariants
- No per-frame managed allocations in gameplay hot path.
- No explicit `Complete()` calls.
- Uses Burst jobs and persistent native containers.
- Sampling is deterministic (`EntityIndexInQuery` stride).

## Performance
- Metrics run only at configured interval (default every 60 frames).
- Per metrics tick complexity:
  - snapshot/build grid: `O(N)`
  - sampled overlap/jam scan: `O(S * k)` where `S` is sampled count and `k` is bounded neighbor checks.

## Verification
1. Enter Play Mode and confirm one startup config log appears: `[HordeTune] cfg ...`.
2. Confirm periodic logs appear:
   - `[HordeTune] dt=... sampled=... overlap=... jam=... sepCap=... pressureCap=...`
3. Verify `sepCap > pressureCap` with current tuning.
4. Confirm Profiler `GC Alloc` remains `0 B` in gameplay loop.
