# ZombieSteeringSystem

## Purpose
Move zombies using prebuilt flow-field directions in-map and gate-seeking outside map bounds.

## Data
- `ZombieTag`, `ZombieMoveSpeed`, `ZombieSteeringState` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `LocalTransform` (Unity.Transforms)
- `MapRuntimeData` + `GatePoint` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`, `Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)
- `FlowFieldSingleton` (`Assets/_Project/Scripts/Map/FlowFieldComponents.cs`)

## Steering Logic
Per zombie update:
1. If outside map: choose nearest gate and normalize `(gate - pos)`.
2. If inside map: read flow byte at current cell and map to 32-direction unit LUT from flow blob.
3. Fallback for `255`/invalid flow: seek center.
4. Propose next position `pos + desired * speed * dt`; reject move only if destination is invalid blocked in-map.

## Invariants
- Zombies never intentionally step onto blocked map cells.
- Zombies outside bounds are directed to nearest gate.
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
2. Confirm zombies spawned outside map first aim for nearest gate and enter map.
3. Confirm in-map zombies follow corridors toward center.
4. Confirm zombies do not traverse blocked cells.
5. Verify behavior remains stable after repeated map regenerations.
