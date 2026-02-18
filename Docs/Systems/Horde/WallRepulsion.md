# WallRepulsionSystem

## Purpose
Prevent zombies from being pushed into blocked map tiles under crowd pressure by using a prebuilt wall field and projection fallback.

## Data
- `WallFieldSingleton` (`Assets/_Project/Scripts/Map/WallFieldComponents.cs`)
- `MapRuntimeData`, `MapWalkableCell` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`)
- `WallRepulsionConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
- Runtime system:
  - `Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`

## Runtime Logic
1. For each zombie, map world position to map cell.
2. If current cell is blocked, project to nearest walkable cell center (small radius search).
3. If walkable and near wall (`wallDist * tileSize < unitRadius`), apply push along wall normal direction.
4. Clamp push by `maxWallPushPerFrame`.
5. Safety-check target cell; if blocked, project again.

## Invariants
- Zombies are corrected out of blocked cells.
- No Unity Physics colliders/rigidbodies required.
- Allocation-free per frame in hot path.

## Verification
1. Enter Play Mode and generate heavy crowd pressure near narrow corridors.
2. Verify zombies no longer clip into blocked tiles.
3. Confirm smooth repulsion near walls under congestion.
4. Profile and confirm `GC Alloc` remains `0 B`.
