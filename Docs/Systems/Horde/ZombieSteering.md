# ZombieSteeringSystem

## Purpose
Move zombies toward map center without full pathfinding while preventing stepping onto cliff tiles.

## Data
- `ZombieTag`, `ZombieMoveSpeed`, `ZombieSteeringState` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- `LocalTransform` (Unity.Transforms)
- `MapRuntimeData` + `MapWalkableCell` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`)

## Steering Logic
Per zombie update:
1. Compute desired direction toward map center.
2. Propose next position `pos + desired * speed * dt`.
3. If destination is inside map and destination tile is cliff, reject it.
4. Evaluate fallback directions from 8 compass directions (N, NE, E, SE, S, SW, W, NW).
5. Pick walkable candidate that reduces squared distance to center the most.
6. If no candidate improves distance, stay in place.

Outside-map movement is allowed so zombies can travel from spawn ring into map entrances.

## Invariants
- Zombies never intentionally step onto blocked map cells.
- Movement remains center-seeking when walkable options exist.
- No path graph or global path cache is required.

## Performance
- Burst-compiled `IJobEntity` update.
- Allocation-free per frame.
- Complexity: `O(zombies * candidates)` where candidates are fixed (9 total including desired).

## Known Limits
- Can stall at local minima near cliffs.
- No inter-zombie collision avoidance.

## Verification
1. Enter Play Mode with map and spawn systems active.
2. Observe zombies move inward toward center.
3. Confirm zombies do not traverse cliff tiles.
4. Verify behavior remains stable across map regenerations for same seed/config.
