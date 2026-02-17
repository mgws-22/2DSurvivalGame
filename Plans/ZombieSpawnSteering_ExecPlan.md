# ZombieSpawnSteering ExecPlan

## Goals
- Spawn zombies in the map spawn ring (outside play area, inside outer spawn square) deterministically.
- Move zombies toward map center with simple cliff-aware steering and no full pathfinding.
- Keep updates Burst/job-friendly and allocation-free in per-frame systems.

## Non-Goals
- Full global pathfinding/navigation mesh.
- Advanced flocking/avoidance.
- Combat/health/targeting behaviors.

## Steps
1. Add ECS data components for zombie tagging, movement, steering state, and spawn config/state.
2. Add map-to-ECS bridge to publish map dimensions/origin/tile-size/spawn-margin and walkable grid buffer.
3. Implement deterministic spawn-ring sampling in `ZombieSpawnSystem` with ECB instantiation.
4. Implement cliff-aware center-seeking in `ZombieSteeringSystem` using 8-direction fallback sampling.
5. Add minimal baker/authoring for zombie prefab and spawn config.
6. Document system purpose, data, invariants, perf constraints, and verification.

## Perf Risks
- Zombie movement cost is `O(zombies)` per frame.
- Spawn system uses main-thread ECB setup and query count each frame, but work is bounded by `maxAlive` and spawn rate.
- Walkability lookup relies on a dense buffer; cache behavior is good but very large maps increase memory footprint.

## Verification
- Spawn points are always outside map bounds and within spawn ring.
- Zombies never step onto cliff cells once inside map.
- Same seed/config produce stable spawn sequence.
