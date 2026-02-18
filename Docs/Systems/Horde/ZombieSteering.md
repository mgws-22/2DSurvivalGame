# ZombieSteeringSystem

## Purpose
Move zombies with one global expanded flow field (map + spawn margin), without gate-seeking logic.

## Data
- `ZombieTag`, `ZombieMoveSpeed`, `ZombieSteeringState` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `LocalTransform` (Unity.Transforms)
- `MapRuntimeData` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`)
- `FlowFieldSingleton` (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)

## Steering Logic
Per zombie update:
1. Convert world position to expanded-flow grid coordinates.
2. If inside expanded bounds: read flow byte and map to 32-direction unit LUT from flow blob.
3. If outside expanded bounds: fallback to center seek direction.
3. Fallback for `255`/invalid flow: seek center.
4. Propose next position `pos + desired * speed * dt`; reject move only if destination is invalid blocked in-map.

## Invariants
- Zombies never intentionally step onto blocked map cells.
- Zombies spawned in margin area can follow flow directly toward center.
- Runtime uses immutable flow blob, no per-zombie path graph solve.

## Performance
- Burst-compiled `IJobEntity` update.
- Allocation-free per frame.
- Complexity: `O(zombies)` in-map, with only a constant-size flow lookup.

## Known Limits
- Can stall at local minima near cliffs.
- No inter-zombie collision avoidance.

## Verification
1. Enter Play Mode with map and spawn systems active.
2. Confirm zombies spawned in outer margin immediately follow flow toward center.
3. Confirm in-map zombies follow corridors toward center.
4. Confirm zombies do not traverse blocked cells.
5. Verify behavior remains stable after repeated map regenerations.
