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
2. If current cell is blocked (inside map), project to nearest point inside a nearby walkable cell (small radius search).
   For boundary wall cells, out-of-map neighbor cells are treated as empty projection candidates so units entering from the spawn margin are corrected back out instead of being pushed through the wall.
3. Sample `WallFieldBlob` using wall-field world origin/cell size (not only map-grid indices), so entities in the out-of-map spawn margin can still evaluate wall distance/normals.
4. If near wall (`wallDist * tileSize < unitRadius`), apply push along wall normal direction.
5. Clamp soft wall push by dt-normalized budget: `min(maxWallPushPerFrame * dt, moveSpeed * dt)`.
6. Safety-check target cell; if target is a blocked map cell, project again with the same nearest-point strategy.

## Invariants
- Zombies are corrected out of blocked cells.
- Blocked-cell projection is a hard safety correction (not speed-capped) so entities cannot remain inside wall tiles.
- Projection uses nearest point inside walkable cells (not center snap) to avoid corner launch/teleport artifacts.
- Boundary blocked-cell projection may use out-of-map cells as empty candidates to preserve outside-of-map movement while preventing boundary wall penetration.
- Units are still allowed to exist outside the map bounds (spawn margin), but boundary wall outer faces repel them and prevent wall/cliff penetration from the outside.
- No Unity Physics colliders/rigidbodies required.
- Allocation-free per frame in hot path.
- `MaxWallPushPerFrame` config is interpreted as units/second and converted to per-frame budget with `dt`.
- Runs after pressure and both separation passes as final wall safety stage.

## Verification
1. Enter Play Mode and generate heavy crowd pressure near narrow corridors.
2. Verify zombies no longer clip into blocked tiles.
3. Verify zombies in the out-of-map spawn margin can move outside the map but do not enter boundary wall/cliff tiles from the outside.
4. Confirm smooth repulsion near internal walls under congestion.
5. Profile and confirm `GC Alloc` remains `0 B`.
