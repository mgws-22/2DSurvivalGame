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
2. If current cell is blocked, project to nearest point inside a nearby walkable cell (small radius search).
3. If walkable and near wall (`wallDist * tileSize < unitRadius`), apply push along wall normal direction.
4. Clamp soft wall push by `min(maxWallPushPerFrame, moveSpeed * dt)`.
5. Safety-check target cell; if blocked, project again with the same nearest-point strategy.

## Invariants
- Zombies are corrected out of blocked cells.
- Blocked-cell projection is a hard safety correction (not speed-capped) so entities cannot remain inside wall tiles.
- Projection uses nearest point inside walkable cells (not center snap) to avoid corner launch/teleport artifacts.
- No Unity Physics colliders/rigidbodies required.
- Allocation-free per frame in hot path.

## Verification
1. Enter Play Mode and generate heavy crowd pressure near narrow corridors.
2. Verify zombies no longer clip into blocked tiles.
3. Confirm smooth repulsion near walls under congestion.
4. Profile and confirm `GC Alloc` remains `0 B`.
