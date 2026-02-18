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
2. Build uniform spatial hash grid (`NativeParallelMultiHashMap<int,int>`).
3. For each zombie, inspect only 3x3 neighbor cells.
4. Accumulate separation correction when neighbor distance is below `minDist = 2*radius`.
5. Clamp correction by `maxPushPerFrame`.
6. Apply corrected positions.

Optional second pass is supported via `Iterations` (clamped to `1..2`).

## Invariants
- Only zombies are moved by separation.
- No per-frame managed allocations.
- No `O(N^2)` all-pairs scan.

## Performance
- Burst jobs for snapshot, grid build, separation, and writeback.
- Complexity: `O(N * k)` where `k` is local neighbors in adjacent cells.
- Persistent native containers reused each frame.

## Verification
1. Enter Play Mode with large zombie count (target 20k+).
2. Observe zombie packs spread without stacking to same point.
3. Profile and confirm `GC Alloc` remains `0 B` in gameplay.
4. Verify no job safety/race exceptions in Console.
