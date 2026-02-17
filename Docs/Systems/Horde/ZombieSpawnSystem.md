# ZombieSpawnSystem

## Purpose
Spawn zombie entities deterministically in the spawn ring around the map.

Spawn ring rule:
- Inside outer square defined by map bounds expanded by `spawnMargin`
- Outside the map play area rectangle

## Data
- `ZombieSpawnConfig` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
  - `SpawnRate` (waves per second)
  - `SpawnBatchSize`
  - `MaxAlive`
  - `Seed`
  - `Prefab`
- `ZombieSpawnState` (`Assets/_Project/Scripts/Horde/ZombieComponents.cs`)
  - persistent deterministic RNG
  - spawn accumulator
- `MapRuntimeData` (`Assets/_Project/Scripts/Map/MapEcsBridge.cs`)
  - map dimensions, origin, tile size, spawn margin

## Algorithm
1. Count current alive zombies (`ZombieTag`).
2. Accumulate `deltaTime * SpawnRate`.
3. Convert whole accumulator units to spawn waves.
4. Clamp spawn count to `MaxAlive - alive`.
5. For each spawn, sample a ring cell using 4-rectangle area-weighted sampling:
- Top strip
- Bottom strip
- Left strip
- Right strip
6. Convert sampled grid cell to world position using map origin + tile size.
7. Instantiate with ECB (`EndSimulationEntityCommandBufferSystem`).

## Determinism
- RNG is seeded from config seed and stored in `ZombieSpawnState`.
- For same seed/config and same update timing, spawn cell sequence is stable.

## Invariants
- Spawned grid positions are never inside play area bounds.
- Spawned positions remain inside spawn ring rectangle.
- Total spawned zombies never exceed `MaxAlive`.

## Performance
- Per-frame work is bounded and allocation-free in hot path.
- Structural changes are batched through ECB only.
- Complexity: `O(1)` overhead + `O(spawnCount)` per frame.

## Authoring
- `ZombieSpawnConfigAuthoring` (`Assets/_Project/Scripts/Horde/ZombieSpawnConfigAuthoring.cs`)
  - place on a scene object
  - set zombie prefab and spawn tuning values

## Verification
1. Enter Play Mode.
2. Confirm zombies appear only outside map bounds and inside spawn ring.
3. Verify no spawn occurs inside playable map rectangle.
4. Keep seed constant and rerun to check stable spawn distribution.
