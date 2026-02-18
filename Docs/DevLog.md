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

## 2026-02-17 - Zombie spawn no-show fix (map ECS sync + one-time diagnostics)

### What was wrong
- `ZombieSpawnSystem` could be blocked by missing `MapRuntimeData` and return before any spawn work.
- `MapEcsBridge.Sync` was called once during map regeneration; if ECS default world was not ready at that moment, sync no-oped and map singleton never appeared.

### What changed
- Added retry-safe map ECS sync:
  - `Assets/_Project/Scripts/Map/MapEcsBridge.cs`
    - `Sync(MapData)` now returns `bool` success/failure.
  - `Assets/_Project/Scripts/Map/MapGenerationController.cs`
    - tracks pending sync and retries in `LateUpdate` until successful.
- Added one-time Editor/Development diagnostics in spawn system:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnSystem.cs`
    - logs once: config presence, prefab validity/tags, map singleton presence + dimensions, spawn params, zombie count.
    - missing requirements now return after emitting that single diagnostic block.

### Why this fixes it
- Guarantees `MapRuntimeData` eventually exists even if initial map generation runs before ECS world creation.
- Diagnostics make missing config/prefab/map immediately visible without runtime log spam.

### How to test
1. Enter Play Mode and check Console for one `ZombieSpawnSystem` diagnostics block.
2. Confirm diagnostics show `ZombieSpawnConfig singleton: yes`, `Prefab entity valid: yes`, and `MapRuntimeData singleton: yes`.
3. Verify zombie entities begin appearing in Entities Hierarchy over time.
4. Verify zombies spawn in spawn ring and not inside map bounds.

## 2026-02-17 - Zombie spawn root-cause fix (missing config singleton)

### Root cause
- Diagnostics showed `ZombieSpawnConfig singleton: no` while map data existed.
- `ZombieSpawnConfigAuthoring` was present in scene, but no runtime config entity was available to spawn system, so it always early-returned.
- With no config singleton, spawn values defaulted to zero and prefab entity remained invalid (`Entity.Null`).

### What changed
- Added runtime config bridge to `ZombieSpawnConfigAuthoring`:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnConfigAuthoring.cs`
  - On Play, it now ensures a `ZombieSpawnConfig` singleton exists in ECS world and keeps values synced.
  - If prefab entity is missing/invalid, it logs a one-time warning and keeps prefab as `Entity.Null` until authoring/baking is valid.
  - Deduplicates accidental multiple `ZombieSpawnConfig` entities to keep singleton semantics.
- Added scene setup utility:
  - `Assets/_Project/Editor/Tools/SurvivalSceneSetupTool.cs`
  - Menu: `Tools/Survival/Setup Zombie Demo Scene`
  - Ensures `ZombieSpawnConfigAuthoring` exists and assigns default prefab/tuning values.
- Improved one-time spawn diagnostics with explicit action hints:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnSystem.cs`

### How to verify
1. In editor, run `Tools > Survival > Setup Zombie Demo Scene`.
2. Enter Play Mode.
3. Console should show exactly one `ZombieSpawnSystem` diagnostics block with actionable status.
4. Entities Hierarchy should contain a `ZombieSpawnConfig` entity with non-zero values and non-null prefab entity.
5. Zombie entity count should increase over time; spawn pattern remains deterministic for same seed/config.

## 2026-02-17 - Zombie visibility fix (prefab baked without render companion)

### Root cause
- Spawn diagnostics could be fully green while zombies were still not visible.
- `ZombieAuthoringBaker` only added gameplay ECS components (`ZombieTag`, speed/state) but did not preserve `SpriteRenderer` on the prefab entity, so spawned zombies could be simulation-only entities.
- Runtime fallback prefab creation in `ZombieSpawnConfigRuntimeBridge` also produced gameplay-only prefab entities with no visual component.

### What changed
- Updated zombie baking to keep visuals on spawned ECS entities:
  - `Assets/_Project/Scripts/Horde/ZombieAuthoring.cs`
  - baker now adds `SpriteRenderer` as a companion component.
- Hardened runtime config bridge to avoid creating invisible fallback prefabs:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnConfigAuthoring.cs`
  - if prefab entity is invalid, config now stays `Entity.Null` and logs actionable warning once in Editor/Development builds.
  - when multiple config entities exist, keeps the one that already has a valid prefab and removes duplicates.
  - actively searches for a baked zombie prefab entity (`Prefab + ZombieTag`) and keeps retrying sync until resolved.
  - editor-only fallback auto-finds a prefab with `ZombieAuthoring` if `_zombiePrefab` is empty.
  - editor runtime fallback requests prefab conversion through `EntityPrefabReference` + `RequestEntityPrefabLoaded`, then binds `PrefabLoadResult.PrefabRoot` into `ZombieSpawnConfig`.
- Extended one-time spawn diagnostics:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnSystem.cs`
  - now logs whether prefab has `SpriteRenderer` companion.
  - logs one first-spawn batch summary (count + first spawn world position) for quick on-screen/off-screen validation.
  - delays one-time diagnostics briefly to avoid false negatives while async prefab load resolves.
- Updated system docs:
  - `Docs/Systems/Horde/ZombieSpawnSystem.md`

### How to verify
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
2. Check one-time spawn diagnostics and confirm `Prefab has SpriteRenderer companion: yes`.
3. Confirm zombie entity count increases over time in Entities Hierarchy.
4. Confirm zombies are visible in Game view and keep moving toward map center.

## 2026-02-17 - Cleanup: remove zombie spawn diagnostics/log noise

### What changed
- Removed one-time spawn diagnostics and first-spawn debug logs from:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnSystem.cs`
- Removed runtime bridge warning logs from:
  - `Assets/_Project/Scripts/Horde/ZombieSpawnConfigAuthoring.cs`

### Why
- System is now stable and the temporary diagnostics were no longer needed.
- Keeps runtime/editor console clean without affecting spawn/steering behavior.

### How to verify
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
2. Confirm zombies still spawn and move as before.
3. Confirm zombie-related diagnostic spam is gone from Console.

## 2026-02-18 - Plan A flow-field pathfinding + gate-seeking steering

### What changed
- Added flow-field ECS data and triggers:
  - `Assets/_Project/Scripts/Map/FlowFieldComponents.cs`
  - `Assets/_Project/Scripts/Map/MapEcsBridge.cs`
- Added flow build system (dirty-triggered BFS build to BlobAsset):
  - `Assets/_Project/Scripts/Map/FlowFieldBuildSystem.cs`
- Updated map runtime payload to include center-open radius:
  - `Assets/_Project/Scripts/Map/MapData.cs`
  - `Assets/_Project/Scripts/Map/MapGenerator.cs`
- Replaced per-zombie local candidate scan with flow/gate steering:
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
- Added/updated docs:
  - `Docs/Systems/Horde/FlowFieldPathfinding.md`
  - `Docs/Systems/Horde/ZombieSteering.md`
  - `Docs/Architecture/Index.md`

### Why
- Needed static-map pathfinding that scales to 20k+ zombies without per-frame path solve.
- Needed deterministic route-to-center behavior from both outside and inside map bounds.
- Needed cache-friendly runtime lookup (`byte` direction field) and one-time build on map regenerate.

### How to test
1. Open `Assets/Scenes/SampleScene.unity`.
2. Enter Play Mode and regenerate map at least once.
3. Confirm one flow build log appears (`FlowField built ...`).
4. Verify zombies spawned outside map move to nearest gate and enter map.
5. Verify in-map zombies follow corridors toward center-open area.
6. Profile gameplay and confirm `GC Alloc` remains `0 B` in hot loop.

## 2026-02-18 - Flow field Scene View gizmo drawer

### What changed
- Added editor-only flow field visualization component:
  - `Assets/_Project/Scripts/Map/Debug/FlowFieldGizmosDrawer.cs`
- Updated map system docs:
  - `Docs/Systems/Map/MapGenerator.md`

### Why
- Needed fast visual verification of direction field output in Scene View.
- Needed debugging toggles without touching runtime hot path for zombie steering.

### How to test
1. Add `FlowFieldGizmosDrawer` to `Map Generation` object.
2. Enter Play Mode and regenerate map once so flow blob exists.
3. Ensure Scene View Gizmos is enabled.
4. Confirm arrows point toward center-open area.
5. Change `sampleStep` and verify draw density changes clearly.
6. Toggle `draw` off and confirm no flow gizmos are rendered.

## 2026-02-18 - Smooth flow directions (32-dir quantized gradient)

### What changed
- Updated flow direction derivation:
  - `Assets/_Project/Scripts/Map/FlowFieldBuildSystem.cs`
  - Replaced 4-dir best-neighbor with blended 8-neighbor gradient.
  - Added diagonal corner-cut prevention.
  - Quantizes to 32 direction indices and stores LUT in `FlowFieldBlob`.
- Updated runtime steering/LUT use:
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
- Updated flow gizmo rendering to use blob LUT:
  - `Assets/_Project/Scripts/Map/Debug/FlowFieldGizmosDrawer.cs`
- Updated docs:
  - `Docs/Systems/Horde/FlowFieldPathfinding.md`
  - `Docs/Systems/Horde/ZombieSteering.md`

### Why
- Reduce stair-step motion from cardinal-only flow.
- Keep runtime cost `O(1)` per zombie (`byte` dir lookup + LUT fetch).

### How to test
1. Enter Play Mode and regenerate map.
2. In Scene View with gizmos enabled, confirm flow arrows show many angles (not only N/E/S/W).
3. Observe zombies in open areas move more naturally diagonally toward center.
4. Confirm no GC alloc spikes in gameplay hot loop.
