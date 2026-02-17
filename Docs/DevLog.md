# DevLog

## 2026-02-17 - Map generation v1 (noise band-pass labyrinth)

### What changed
- Added map generation runtime module:
  - `Assets/_Project/Scripts/Map/TileType.cs`
  - `Assets/_Project/Scripts/Map/MapConfig.cs`
  - `Assets/_Project/Scripts/Map/MapData.cs`
  - `Assets/_Project/Scripts/Map/MapGenerator.cs`
  - `Assets/_Project/Scripts/Map/MapTilemapRenderer.cs`
  - `Assets/_Project/Scripts/Map/MapGenerationController.cs`
- Added implementation plan:
  - `Plans/MapGenerationV1_ExecPlan.md`
- Added system documentation:
  - `Docs/Systems/Map/MapGenerator.md`
  - `Docs/Architecture/Index.md`

### Why
- Needed deterministic dense labyrinth maps with walkable/blocked tiles.
- Needed side gates and enforced gate-to-center connectivity for external spawn access.
- Needed play-area tilemap rendering plus spawn-ring bounds data/debug.

### How to test
1. Open `Assets/Scenes/SampleScene.unity`.
2. Enter Play Mode.
3. Verify map renders as two tile types (ground/cliff) and center area is open.
4. Select `Map Generation` object and confirm side gate gizmos on all sides.
5. Confirm spawn bounds gizmo encloses and extends beyond play bounds.
6. Trigger `Regenerate Map` from component context menu and verify map rebuilds deterministically for same seed.

## 2026-02-17 - Cliff autotiling via 4-neighbor mask

### What changed
- Added cliff autotile data asset:
  - `Assets/_Project/Scripts/Map/CliffTileSet.cs`
- Added shared placeholder texture generator:
  - `Assets/_Project/Scripts/Map/CliffTileTextureFactory.cs`
- Updated tilemap rendering to use cliff mask selection (N/E/S/W ground-neighbor mask):
  - `Assets/_Project/Scripts/Map/MapTilemapRenderer.cs`
- Added editor asset generation tool:
  - `Assets/_Project/Editor/Tools/MapPlaceholderCliffTileGenerator.cs`
- Updated map system docs:
  - `Docs/Systems/Map/MapGenerator.md`

### Why
- Improve map readability in dense labyrinth layouts by adding visible cliff edges/corners.
- Avoid manual RuleTile setup and keep placeholder art generation code-driven.

### How to test
1. In Unity, run `Tools > Map > Generate Placeholder Cliff Tiles`.
2. Confirm assets are generated under `Assets/_Project/Art/Generated/Tiles/Cliff/`.
3. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
4. Verify cliffs now show border/edge variation around corridors.
5. Trigger `Regenerate Map` and confirm output remains deterministic for the same seed/config.

## 2026-02-17 - Zombie spawn ring + center steering (ECS)

### What changed
- Added map-to-ECS runtime bridge:
  - `Assets/_Project/Scripts/Map/MapEcsBridge.cs`
  - `Assets/_Project/Scripts/Map/MapGenerationController.cs` (sync call on regenerate)
- Added zombie ECS components and authoring:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - `Assets/_Project/Scripts/Horde/ZombieAuthoring.cs`
  - `Assets/_Project/Scripts/Horde/ZombieSpawnConfigAuthoring.cs`
- Added zombie ECS systems:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnSystem.cs`
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
- Added plan + docs:
  - `Plans/ZombieSpawnSteering_ExecPlan.md`
  - `Docs/Systems/Horde/ZombieSpawnSystem.md`
  - `Docs/Systems/Horde/ZombieSteering.md`
  - `Docs/Architecture/Index.md` (links)
  - `Docs/Systems/Map/MapGenerator.md` (ECS bridge section)

### Why
- Needed deterministic zombie spawning in spawn ring (outside map bounds).
- Needed simple center-seeking movement that does not step onto cliff cells.
- Needed Burst-friendly, allocation-free ECS loop with no pathfinding dependency.

### How to test
1. Ensure Entities package is installed/enabled in the project.
2. Add `ZombieAuthoring` to zombie prefab and set move speed.
3. Add `ZombieSpawnConfigAuthoring` to a scene object and assign zombie prefab/config values.
4. Enter Play Mode and confirm zombies spawn in outer ring, never inside play area at spawn.
5. Observe zombies move toward map center and do not step onto cliff tiles.
6. Keep map seed + spawn seed fixed and rerun to validate deterministic spawn pattern.

## 2026-02-17 - ECS authoring compile guard fix (UNITY_ENTITIES)

### What was broken
- ECS gameplay files were wrapped in `#if UNITY_ENTITIES`.
- In this project compile context, `UNITY_ENTITIES` was not defined, so entire files were compiled out.
- Result: bakers (including `ZombieAuthoringBaker`) did not compile/run, and expected baked ECS entities did not appear.

### What changed
- Removed `#if UNITY_ENTITIES` guards from gameplay-critical ECS files so they always compile with installed Entities package:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - `Assets/_Project/Scripts/Horde/ZombieAuthoring.cs`
  - `Assets/_Project/Scripts/Horde/ZombieSpawnConfigAuthoring.cs`
  - `Assets/_Project/Scripts/Horde/ZombieSpawnSystem.cs`
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
  - `Assets/_Project/Scripts/Map/MapEcsBridge.cs`
- Added architecture note:
  - `Docs/Architecture/ECSCompilation.md`
  - linked from `Docs/Architecture/Index.md`

### Why this fix
- The project already depends on `com.unity.entities`.
- Unconditional compilation avoids hidden failures caused by missing custom symbols and ensures bakers remain active.

### How to test
1. Let Unity recompile scripts.
2. Verify `ZombieAuthoring.cs` is no longer greyed out by preprocessor exclusion.
3. Enter Play Mode with `ZombieAuthoring` prefab + `ZombieSpawnConfigAuthoring` in scene.
4. Open Entities Hierarchy and search for `ZombieTag` / `ZombieMoveSpeed`.
5. Confirm baked/spawned entities are present.
