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
4. Schedules `CopyPressureSnapshotJob` (`IJob`) that copies singleton `DynamicBuffer<PressureCell>` into persistent `NativeArray<float> _pressureSnapshot` for the current flow cell count.
5. Parallel metrics evaluation reads pressure only from `_pressureSnapshot` (never from `DynamicBuffer` in parallel job) and computes backpressure scale:
   - `excess = max(0, localPressure - BackpressureThreshold)`
   - `speedScale = clamp(1 / (1 + BackpressureK * excess), MinSpeedFactor, MaxSpeedFactor)`
6. `overlap(sample)%`:
   - sampled zombie counts as overlap if any nearby neighbor is closer than `2 * Radius`.
7. `jam%` (cheap approximation):
   - sampled zombie is jammed if:
     - sampled local pressure is above `BackpressureThreshold`, and
     - sampled displacement speed between metric ticks is below `moveSpeed * 0.2`.
8. Collects observed speed statistics from sampled displacement using per-thread counters and a fixed 32-bin histogram (`0..2 units/s`) for percentile approximation.
9. Collects solver-limit diagnostics from sampled neighbor scan:
   - `capReachedHits%`: sampled units that hit `MaxNeighbors` cap.
   - `avgProcessedNeighbors`: average processed neighbors per sampled unit.
   - `hardJamEnabled%`: sampled units matching hard-jam condition (`localPressure > threshold` or `dense && slow`).
10. Logs one `[HordeTune]` line per metrics tick in Editor/Development builds:
   - `logIntervalSeconds` for sampling window length
   - `simDt` for current simulation frame dt
   - overlap/jam percentages
   - speed stats (`avg/p50/p90/min/max`)
   - average speed fraction (`observedSpeed / moveSpeed`)
   - backpressure activity (`active%`, `avgScale`, `minScale`)
   - solver-limit diagnostics (`capReachedHits%`, `avgProcessedNeighbors`, `hardJamEnabled%`)
   - per-frame cap estimates (`sepCapFrame`, `pressureCapFrame`) plus raw pressure/separation config values.

## Invariants
- No per-frame managed allocations in gameplay hot path.
- No explicit `Complete()` calls.
- Uses Burst jobs and persistent native containers.
- Sampling is deterministic (`EntityIndexInQuery` stride).
- Histogram and all thread accumulators are persistent native arrays (no per-tick allocations).
- Thread counter arrays are resized only when `JobsUtility.MaxJobThreadCount` changes, then reused.
- No `DynamicBuffer<PressureCell>` access in parallel metrics jobs.
- Pressure buffer access is isolated to one single-thread snapshot copy job and dependency-chained.

## Performance
- Metrics run only at configured interval (default every 60 frames).
- Per metrics tick complexity:
  - snapshot/build grid: `O(N)`
  - sampled overlap/jam scan: `O(S * k)` where `S` is sampled count and `k` is bounded neighbor checks.

## Verification
1. Enter Play Mode and confirm one startup config log appears: `[HordeTune] cfg ...`.
2. Confirm periodic logs appear:
   - `[HordeTune] logIntervalSeconds=... simDt=... sampled=... overlap=... jam=... speed(... ) frac(... ) backpressure(pressureThreshold=... k=... active=... avgScale=... minScale=...) ...`
3. Verify `sepCap > pressureCap` with current tuning.
4. Verify `backpressure(active=...)` stays low in open flow and rises in chokepoint jams.
5. Confirm Profiler `GC Alloc` remains `0 B` in gameplay loop.
