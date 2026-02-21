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

## 2026-02-18 - Expanded flow field (map + spawn margin), gate-seeking removed

### What changed
- Reworked flow build to cover expanded grid including spawn margin:
  - `Assets/_Project/Scripts/Map/FlowFieldBuildSystem.cs`
  - expanded dimensions: `width + 2*spawnMargin`, `height + 2*spawnMargin`
  - expanded origin: `mapOrigin - spawnMargin * tileSize`
  - outside-map expanded cells are walkable
- Removed gate-pathing ECS data from runtime bridge:
  - `Assets/_Project/Scripts/Map/MapEcsBridge.cs`
  - removed `GatePoint` buffer publishing
- Removed gate-seeking runtime steering:
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
  - steering now uses expanded flow lookup everywhere; outside expanded bounds falls back to center seek
- Removed `GatePoint` component type:
  - `Assets/_Project/Scripts/Map/FlowFieldComponents.cs`
- Updated docs:
  - `Docs/Systems/Horde/FlowFieldPathfinding.md`
  - `Docs/Systems/Horde/ZombieSteering.md`
  - `Docs/Systems/Map/MapGenerator.md`

### Why
- Needed one global flow field from spawn ring to center, without special-case gate routing.
- Keeps runtime lookup constant-time while avoiding stalls in outside-map spawn area.

### How to test
1. Enter Play Mode and regenerate map.
2. Confirm flow gizmo bounds include spawn margin area.
3. Spawn zombies in outer ring and verify they immediately follow flow inward without gate targeting.
4. Confirm `GC Alloc` remains `0 B` in hot gameplay loop.

## 2026-02-18 - Baking + gizmo teardown stability fixes

### What changed
- Removed duplicate `SpriteRenderer` companion add in zombie baker:
  - `Assets/_Project/Scripts/Horde/ZombieAuthoring.cs`
- Hardened flow gizmo query cleanup during editor world teardown:
  - `Assets/_Project/Scripts/Map/Debug/FlowFieldGizmosDrawer.cs`

### Why
- Baking error came from adding `SpriteRenderer` companion twice (already added by `SpriteRendererCompanionBaker`).
- Editor disable path could dispose stale query handles after world destruction.

### How to test
1. Recompile and enter Play Mode.
2. Confirm no baking error about duplicate `SpriteRenderer`.
3. Toggle/disable object with `FlowFieldGizmosDrawer` and confirm no teardown exception appears.

## 2026-02-18 - No-overlap horde separation (ECS spatial hash, no physics)

### What changed
- Added separation config component:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - `HordeSeparationConfig` with defaults (`radius=0.25`, `strength=0.7`, `maxPush=0.15`, `iterations=1`)
- Added separation runtime system:
  - `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`
  - runs after `ZombieSteeringSystem`
  - snapshots positions, builds spatial hash, resolves local overlaps, writes corrected transforms
- Added docs:
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Architecture/Index.md` link

### Why
- Needed scalable no-overlap behavior for 20k+ zombies without Unity Physics.
- Avoided `O(N^2)` by using neighbor lookups in a uniform grid.

### How to test
1. Enter Play Mode and raise zombie count toward stress levels.
2. Confirm zombies no longer stack on identical positions.
3. Confirm player remains unaffected (separation applies only to `ZombieTag` query).
4. Profile gameplay and verify `GC Alloc` stays `0 B`.

## 2026-02-18 - Separation robustness in congestion (bounded neighbor work)

### What changed
- Updated separation config:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - added `CellSizeFactor`, `InfluenceRadiusFactor`, `MaxNeighbors`
- Updated separation runtime:
  - `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`
  - default tuned for small units: `Radius=0.05`
  - `cellSize = minDist * CellSizeFactor`
  - early cull by `InfluenceRadius`
  - hard cap `MaxNeighbors` per zombie with early loop break
  - bounded push via `MaxPushPerFrame`
  - singleton config creation made safe (create only if missing)
- Updated docs:
  - `Docs/Systems/Horde/HordeSeparation.md`

### Why
- Prevent neighbor explosion and frame spikes when many zombies cluster in the same grid cells.
- Keep per-zombie separation work bounded and predictable for 20k+ stress cases.

### How to test
1. Create dense crowd clustering near center.
2. Verify frame-time spikes are reduced versus uncapped separation.
3. Confirm overlap reduction remains acceptable under heavy load.
4. Confirm `GC Alloc` remains `0 B` in gameplay.

## 2026-02-18 - WallField + wall repulsion safety net

### What changed
- Added wall field ECS data + dirty tag:
  - `Assets/_Project/Scripts/Map/WallFieldComponents.cs`
- Added wall field build system:
  - `Assets/_Project/Scripts/Map/WallFieldBuildSystem.cs`
  - computes wall distance + quantized outward normal per cell
- Hooked map sync to trigger wall field rebuild:
  - `Assets/_Project/Scripts/Map/MapEcsBridge.cs`
- Added runtime wall repulsion/correction:
  - `Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`
  - includes blocked-cell projection fallback
- Added config:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs` (`WallRepulsionConfig`)
- Added docs:
  - `Docs/Systems/Horde/WallRepulsion.md`
  - `Docs/Architecture/Index.md`

### Why
- Crowd pressure could force zombies into blocked tiles without a wall-aware correction step.
- Needed cheap O(1) wall avoidance per zombie and robust fallback when overlap pressure is extreme.

### How to test
1. Spawn dense zombie groups in narrow corridors.
2. Verify zombies are repelled from walls and corrected out of blocked cells.
3. Confirm no per-frame allocations in profiler (`GC Alloc` stays `0 B`).

## 2026-02-18 - Speed cap tied to moveSpeed for soft crowd pushes

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - zombie query now explicitly requires `ZombieMoveSpeed`.
  - separation soft correction stays capped by `min(MaxPushPerFrame, moveSpeed * dt)` (per-iteration budget).
- Updated `Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`:
  - near-wall soft push now capped by `min(MaxWallPushPerFrame, moveSpeed * dt)`.
  - blocked-cell projection fallback remains uncapped hard correction for wall safety.
- Updated docs:
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Systems/Horde/WallRepulsion.md`

### Why
- Prevent effective movement speed spikes under dense separation/wall pressure while preserving the invariant that zombies cannot remain inside blocked tiles.

### How to test
1. Spawn dense clusters near walls/corridors.
2. Verify no visible rocket-push bursts; movement remains bounded by normal move speed.
3. Confirm zombies are still projected out immediately if they enter blocked cells.
4. Profile gameplay: `GC Alloc` remains `0 B`.

## 2026-02-18 - Wall corner stability fix (remove center-snap projection jumps)

### What changed
- Updated `Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`:
  - blocked-cell projection now snaps to the nearest point inside candidate walkable cells instead of always snapping to walkable cell centers.
  - keeps existing hard safety behavior (still guarantees correction out of blocked cells), but reduces large correction distances in cliff corners.
- Updated docs:
  - `Docs/Systems/Horde/WallRepulsion.md`

### Why
- Corner/cliff pressure could trigger visible instant pushes when fallback projection chose a walkable cell center significantly away from the penetrated point.

### How to test
1. Drive dense zombie groups into cliff corners and narrow wall bends.
2. Verify correction remains immediate but without large outward launch jumps.
3. Confirm zombies still never remain inside blocked cells.
4. Profile gameplay and confirm `GC Alloc` remains `0 B`.

## 2026-02-18 - Hard separation solver added (Jacobi/PBD, default disabled)

### What changed
- Added hard separation config component:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - new `HordeHardSeparationConfig` (`Enabled`, `Radius`, `CellSize`, `MaxNeighbors`, `Iterations`, `MaxCorrectionPerIter`, `Slop`)
- Added scene authoring + baker for singleton config:
  - `Assets/_Project/Scripts/Horde/HordeHardSeparationConfigAuthoring.cs`
  - default `Enabled = 0` to keep gameplay unchanged unless manually toggled.
- Added runtime hard separation system:
  - `Assets/_Project/Scripts/Horde/HordeHardSeparationSystem.cs`
  - snapshots positions, builds spatial hash, runs capped-neighbor Jacobi iterations, writes back final positions.
  - deterministic fallback normal for near-zero pair distance.
  - profiler markers added for `BuildGrid`, `IterationCompute`, `IterationApply`, `WriteBack`.
- Added docs:
  - `Docs/Systems/Horde/HordeHardSeparation.md`
  - `Docs/Architecture/Index.md`

### Why
- Needed a stronger no-overlap option than soft push while preserving ECS/Burst scaling and race safety.
- Kept it opt-in (`Enabled = 0`) so existing behavior does not change by default.

### How to test
1. Add `HordeHardSeparationConfigAuthoring` to a scene object.
2. Enter Play Mode with `Enabled` left off and confirm current behavior is unchanged.
3. Toggle `Enabled` on and verify dense zombie overlap is reduced.
4. Profile hot gameplay and confirm `GC Alloc` stays `0 B` with no job safety errors.

## 2026-02-18 - Hard solver runtime wiring (order + soft-solver gating)

### What changed
- Updated hard solver order and runtime diagnostics:
  - `Assets/_Project/Scripts/Horde/HordeHardSeparationSystem.cs`
  - added `UpdateBefore(WallRepulsionSystem)` (while keeping `UpdateAfter(ZombieSteeringSystem)`).
  - added one-time log when hard solver starts running (`Enabled=1`).
- Added soft solver gate to prevent double correction:
  - `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`
  - early return when `HordeHardSeparationConfig` exists and `Enabled != 0`.
  - missing hard config is treated as hard-disabled (soft solver runs normally).
  - added one-time log when soft solver is skipped due to hard solver being enabled.
- Updated docs:
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Systems/Horde/HordeHardSeparation.md`

### Why
- Ensure intended runtime pipeline: steering -> hard separation (when enabled) -> wall repulsion.
- Avoid soft+hard separation running together and over-correcting zombie positions.

### How to test
1. Leave `HordeHardSeparationConfig.Enabled = 0` (or remove the config) and verify soft separation still behaves as before.
2. Set `Enabled = 1` and confirm:
   - one-time log from hard solver appears,
   - one-time soft-solver skip log appears,
   - wall repulsion still executes after separation.
3. Confirm no compilation errors, job safety warnings, or GC spikes in gameplay.

## 2026-02-18 - Density/pressure congestion field on expanded flow grid

### What changed
- Added pressure-field config:
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - new `HordePressureConfig` (`Enabled`, density target, push caps, blur/tick settings, soft-separation gating flag)
- Added pressure-field runtime system:
  - `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`
  - runs after steering and before separation solvers
  - builds per-cell density on the expanded `FlowFieldBlob` grid
  - converts density to pressure (+ blocked-cell penalty), optional blur, applies bounded anti-pressure push per zombie
  - rejects pressure moves into blocked expanded cells
- Added optional soft pairwise separation gating:
  - `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`
  - skips soft solver when pressure config is enabled and `DisablePairwiseSeparationWhenPressureEnabled != 0`
- Added plan and docs:
  - `Plans/HordePressureField_ExecPlan.md`
  - `Docs/Systems/Horde/HordePressureField.md`
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Architecture/Index.md`

### Why
- Pairwise-only local separation can still create long-lived crowd compression in chokepoints and near walls.
- Needed an `O(N + G)` congestion signal (`N` entities, `G` expanded-grid cells) aligned with the same expanded flow grid used for center steering.
- Needed bounded steering influence so movement remains speed-capped while wall projection remains final blocked-cell safety.

### How to test
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
2. Spawn dense hordes near narrow corridors and wall bends.
3. Verify crowds gradually fan out instead of staying permanently jammed.
4. Verify zombies still converge to center (single-point target unchanged).
5. Verify zombies are not left inside blocked map tiles after wall repulsion.
6. Profile hot loop and confirm `GC Alloc` remains `0 B` with no new sync-point stalls.

## 2026-02-18 - Pressure anti-stack fix (deterministic spread + safer default)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - default `DisablePairwiseSeparationWhenPressureEnabled` changed to `0` (augment mode by default).
  - pressure direction now includes deterministic per-entity spread bias when many units share same local pressure.
  - fallback direction now uses deterministic unit direction instead of zero vector.
  - update order is steering -> pressure -> separation/hard separation -> wall safety.
- Updated `Assets/Scenes/SampleScene.unity`:
  - `HordeHardSeparationConfigAuthoring._enabled` set to `0` so sample scene validates pressure+soft behavior by default.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`

### Why
- In dense same-cell cases, many units could pick identical pressure direction and continue moving as one compact stack.
- Augment-by-default plus deterministic spread reduces persistent clumping while preserving Burst/jobified behavior and wall safety.

### How to test
1. Enter Play Mode and spawn ~200+ zombies in a tight group.
2. Confirm the group fans out over time instead of remaining a single compact stack/column.
3. Verify units still move toward center and are not left in blocked tiles.
4. Profile and confirm `GC Alloc` stays `0 B`.

## 2026-02-18 - Soft separation zero-distance fix (exact overlap unstuck)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - exact-overlap neighbor pairs (`distSq == 0`) are no longer ignored.
  - added deterministic fallback normal for zero-distance pairs, keyed by entity pair + iteration.
  - per-iteration index now passed into separation job for deterministic tie-breaking.
- Updated docs:
  - `Docs/Systems/Horde/HordeSeparation.md`

### Why
- Repeated spawns/stacking can produce identical positions; previous soft solver skipped those pairs and they could remain clumped in a single point/line.
- Deterministic zero-distance handling keeps separation Burst-safe, allocation-free, and stable across frames.

### How to test
1. Spawn 200+ zombies with intentionally dense overlap.
2. Verify previously stacked units now begin separating instead of remaining in one point.
3. Confirm movement still heads toward center and wall projection still prevents blocked-tile residency.
4. Profile gameplay: `GC Alloc` remains `0 B`.

## 2026-02-18 - Pressure center-lock fix (center-away bias + pairwise augmentation default)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - default `DisablePairwiseSeparationWhenPressureEnabled` set to `0` so soft separation augments pressure by default.
  - increased default pressure speed budget (`SpeedFractionCap = 1`) so anti-jam push can match base movement budget.
  - `ApplyPressureJob` now uses `EntityIndexInQuery` for deterministic per-entity tie-breaking.
  - tie/symmetry fallback now biases away from `MapRuntimeData.CenterWorld` plus deterministic jitter, instead of returning zero.
  - pressure gradient / best-neighbor / wall-gradient directions now include spread bias to avoid lockstep columns.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`

### Why
- Dense groups near center can create near-symmetric local pressure where many entities choose identical directions or no direction.
- Center-away deterministic bias prevents point-lock while preserving single-point goal steering, Burst compatibility, and allocation-free hot paths.

### How to test
1. Enter Play Mode and let ~200+ zombies converge toward center.
2. Confirm they do not remain pinned in one exact point/column.
3. Verify wall safety still holds (no entities left in blocked tiles).
4. Profile and confirm `GC Alloc` remains `0 B`.

## 2026-02-18 - Pressure motion stabilization (remove "extra force" feel)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - retuned defaults to softer pressure steering (`TargetUnitsPerCell=2.5`, `PressureStrength=0.75`, `MaxPushPerFrame=0.12`, `SpeedFractionCap=0.4`).
  - removed strong center-away fallback from pressure tie handling.
  - added flow-alignment constraint so pressure steering cannot strongly oppose local flow-to-center direction.
  - kept deterministic tie-break bias but reduced it to a mild spread term.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`

### Why
- Previous anti-lock tuning solved point clumping but produced unnatural pathing that looked like an external force field.
- Pressure should act as local decongestion, not primary steering; flow-to-center should stay dominant.

### How to test
1. Enter Play Mode with high unit counts.
2. Verify units still spread under congestion but overall trajectories remain center-seeking and natural.
3. Confirm no long straight "force-field" lanes are formed in open areas.
4. Profile and confirm `GC Alloc` remains `0 B`.

## 2026-02-21 - Pressure+separation augment mode lock-in + runtime diagnostics

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - removed pressure-based auto-skip gating; pressure no longer disables soft separation.
  - removed hard-solver skip gating so soft and hard separation can run in the same frame when configured.
  - set deterministic order attributes to run after pressure and before hard separation.
  - restored conservative soft defaults (`Radius=0.05`, `CellSizeFactor=1.25`, `InfluenceRadiusFactor=1.5`, `SeparationStrength=0.7`, `MaxPushPerFrame=0.12`, `MaxNeighbors=24`).
  - added one-time Editor/Development runtime diagnostics log with pressure/soft/hard active flags and effective order.
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - kept augment default (`DisablePairwiseSeparationWhenPressureEnabled=0`).
  - set conservative default pressure cap (`SpeedFractionCap=0.25`).
  - pressure anti-backtrack now uses projection rule: remove only negative component vs `flowDir`.
- Updated separation/wall ordering:
  - `Assets/_Project/Scripts/Horde/HordeHardSeparationSystem.cs` now runs after pressure + soft separation.
  - `Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs` explicitly runs after pressure + soft + hard as final wall safety.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Systems/Horde/HordeHardSeparation.md`
  - `Docs/Systems/Horde/WallRepulsion.md`

### Why
- Needed strict augment behavior: pressure is macro decongestion, separation remains micro contact resolution.
- Needed deterministic update ordering to avoid perceived "systems fighting".
- Needed runtime truth visibility (one log) to confirm active toggles and ordering during Play.

### How to test
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
2. Confirm one diagnostics log appears once in Console and includes pressure/soft/hard states plus order.
3. With 200+ then 5k+ units, verify:
   - pressure and separation both visibly contribute,
   - movement remains center-driven (no strong backward pressure drift),
   - units do not remain inside blocked cells after wall safety stage.
4. Profile gameplay and confirm `GC Alloc` remains `0 B`.

## 2026-02-21 - Push budgets converted to dt-normalized units/second

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - `MaxPushPerFrame` is now interpreted as units/second and converted to per-frame budget (`MaxPushPerFrame * dt`).
  - pressure clamp now effectively uses `min(MaxPushPerFrame * dt, moveSpeed * dt * SpeedFractionCap)`.
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - separation max correction budget now uses `MaxPushPerFrame * dt`.
  - per-iteration cap now splits both config and moveSpeed budgets across iterations.
  - one-time diagnostics log now includes `dt`, reference max step, and computed per-frame budgets for pressure/separation/wall.
- Updated `Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`:
  - wall soft push budget now uses `MaxWallPushPerFrame * dt`.
  - blocked-cell projection remains uncapped hard safety correction.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Systems/Horde/WallRepulsion.md`

### Why
- Previous frame-based budget interpretation made crowd behavior FPS-dependent (not stable between ~60 and ~300 FPS).
- Dt-normalized budgets preserve per-second behavior while keeping wall safety guarantees.

### How to test
1. Enter Play Mode with unlocked FPS and observe crowd behavior over time.
2. Cap to ~60 FPS and compare over equal wall-clock time; spreading/jamming should be qualitatively similar.
3. Confirm one diagnostics log appears and includes `dt` plus computed frame budgets.
4. Verify no blocked-cell residency after wall safety stage and `GC Alloc` remains `0 B`.

## 2026-02-21 - HordeSeparation job safety fix for spatial grid clear

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - replaced main-thread `_cellToIndex.Clear()` inside the iteration loop with a scheduled `ClearSpatialGridJob`.
  - clear now runs in the same dependency chain before each `BuildSpatialGridJob`.

### Why
- `NativeParallelMultiHashMap` was being cleared on the main thread while a prior scheduled `BuildSpatialGridJob` still had write access, triggering `InvalidOperationException`.
- The fix removes the race without adding `Complete()` or new sync points.

### How to test
1. Enter Play Mode with `HordeSeparationConfig.Iterations = 2`.
2. Confirm the previous exception about `BuildSpatialGridJob.Grid` and `NativeParallelMultiHashMap.Clear()` no longer appears.
3. Observe normal separation behavior and verify `GC Alloc` remains `0 B`.

## 2026-02-21 - Pressure diag cap accuracy + thread-safe density accumulation

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs` one-time runtime diagnostics:
  - log now reports pressure budgets explicitly as:
    - `PressureConfigBudgetThisFrame = MaxPushPerFrame * dt`
    - `PressureSpeedBudgetThisFrame = RefMaxStep * SpeedFractionCap`
    - `PressureEffectiveCapThisFrame = min(configBudget, speedBudget)`
  - log also prints `PressureMaxPushPerFrame` and `PressureSpeedFractionCap` values from config.
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - `AccumulateDensityJob` now uses atomic increment (`Interlocked.Increment` on density cell pointer via `UnsafeUtility.AsRef`) and is scheduled with `ScheduleParallel`.
  - removes non-atomic read-modify-write (`Density[index] = Density[index] + 1`) race under high concurrency.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`

### Why
- Pressure diagnostics needed to match the real runtime clamp so tuning is trustworthy.
- Density accumulation needed to be thread-safe at high entity counts (20k+) while keeping Burst/jobs and zero per-frame allocations.

### How to test
1. Enter Play Mode and check the one-time `[HordeRuntimeDiag]` line.
2. Verify:
   - `PressureConfigBudgetThisFrame` and `PressureSpeedBudgetThisFrame` are printed.
   - `PressureEffectiveCapThisFrame` equals their `min(...)`.
   - with `dt≈0.022`, `RefMoveSpeed=1`, `SpeedFractionCap=0.25`, effective cap is near `0.0055` when config budget is larger.
3. Stress with 5k+ to 20k+ entities and confirm pressure density behavior is stable (no obvious random undercount artifacts).
4. Profile and confirm `GC Alloc` remains `0 B`.

## 2026-02-21 - Pressure density accumulation switched to per-thread bins + reduce (no unsafe)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - removed all `unsafe`/pointer-based density accumulation.
  - added persistent `_densityPerThread` (`cellCount * workerCount`) and `_workerCount` (`JobsUtility.MaxJobThreadCount`).
  - rebuild chain now does:
    - clear `_densityPerThread`,
    - parallel accumulate into per-thread slices using `[NativeSetThreadIndex]`,
    - parallel reduce pass into final `_density`.
  - kept the existing dependency chain scheduling (no `Complete()`).

### Why
- Needed thread-safe high-throughput density accumulation without enabling `/unsafe`.
- Needed to keep accumulation parallel for large crowds (20k+) and avoid single-thread bottlenecks.

### How to test
1. Confirm project compiles with "Allow unsafe code" disabled.
2. Run Play Mode with 5k+ to 20k+ units and ensure pressure behavior is stable.
3. Check one-time runtime diag still prints pressure cap math values.
4. Profile and verify `GC Alloc` remains `0 B` in gameplay loop.

## 2026-02-21 - Separation authority tuning + quick overlap/jam metrics

### What changed
- Confirmed/kept soft separation default cap at `MaxPushPerFrame = 0.40` (units/second, dt-scaled in runtime), with `Iterations = 2` and `MaxNeighbors = 24`.
- Added quick tuning metrics runtime components in `Assets/_Project/Scripts/Horde/ZombieComponents.cs`:
  - `HordeTuningQuickConfig`
  - `HordeTuningQuickMetrics`
- Added `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`:
  - runs after `WallRepulsionSystem`
  - samples every Nth entity (`SampleStride`) every `LogEveryNFrames`
  - computes sampled overlap and jam counters using a bounded local grid-neighbor scan
  - logs `[HordeTune]` once per metrics tick in Editor/Development builds
  - uses persistent native containers only, no explicit `Complete()`
- Updated docs:
  - `Docs/Systems/Horde/HordeSeparation.md`
  - `Docs/Systems/Horde/HordeTuningQuickMetrics.md`
  - `Docs/Architecture/Index.md`

### Why
- Needed soft separation to have stronger correction authority than pressure near high-density sinks.
- Needed lightweight data-driven observability (overlap/jam percentages) for iterative tuning.

### How to test
1. Enter Play Mode and check one-time `[HordeRuntimeDiag]` values:
   - `SeparationMaxThisFrame` should be higher than `PressureEffectiveCapThisFrame`.
2. Confirm `[HordeTune] cfg ...` startup log appears once.
3. Confirm periodic `[HordeTune]` logs appear with sampled/overlap/jam plus `sepCap`/`pressureCap`.
4. Profile and verify `GC Alloc` remains `0 B` in gameplay loop.

## 2026-02-21 - Data-driven tuning iteration (run data)
- Raised `HordeSeparationConfig.MaxPushPerFrame` from `0.40` to `0.60` (units/second) and `InfluenceRadiusFactor` from `1.75` to `2.00`.
- Reason: `[HordeTune]` overlap stayed far above target (>8%, trending to 100%), so Rule 1 was applied to increase soft separation authority and widen local neighbor response.

## 2026-02-21 - Data-driven tuning iteration (run data 2)
- Raised `HordeSeparationConfig.MaxPushPerFrame` from `0.60` to `0.75` (units/second, Rule 1).
- Reason: overlap remained very high while `InfluenceRadiusFactor` was already at max `2.0`, so only soft cap was increased this pass.

## 2026-02-21 - HordeTune cap log fix + minimal pressure backpressure
- Updated `HordeTuningQuickMetricsSystem` logging to print `logIntervalSeconds` (window) and `simDt` (frame), and report per-frame caps (`sepCapFrame`, `pressureCapFrame`) computed from `simDt` plus raw config values.
- Added minimal backpressure in `HordePressureFieldSystem` (`MinSpeedFactor=0.15`, `BackpressureK=0.35`) so high local pressure reduces net forward inflow toward the point target.
- Purpose: keep diagnostics comparable with `HordeRuntimeDiag` and reduce runaway sink jamming over time without changing target semantics.

## 2026-02-21 - Backpressure threshold/ramp in steering + richer HordeTune metrics

### What changed
- Moved backpressure speed scaling to steering (instead of pressure displacement):
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
  - uses local pressure with threshold+ramp formula:
    - `excess = max(0, localPressure - BackpressureThreshold)`
    - `speedScale = clamp(1 / (1 + BackpressureK * excess), MinSpeedFactor, BackpressureMaxFactor)`
- Published active pressure grid to ECS buffer for read-only consumers:
  - `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`
  - `PressureFieldBufferTag` + `DynamicBuffer<PressureCell>` updated when pressure field rebuilds.
- Extended quick tuning metrics with speed/backpressure stats:
  - `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`
  - `Assets/_Project/Scripts/Horde/ZombieComponents.cs`
  - added sampled speed stats (`avg/p50/p90/min/max`), speed fraction average, backpressure active%, avg/min speed scale.

### Why
- Prevent global slowdown in normal flow and only apply slowdown in truly congested cells.
- Make tuning data directly explainable: overlap/jam + observed speed + backpressure activity in one line.

### How to test
1. Enter Play Mode with point-target stress scenario.
2. Confirm `[HordeTune]` now prints `speed(...)`, `frac(...)`, and `backpressure(active=... avgScale=... minScale=...)`.
3. In open areas verify `backpressure(active=...)` is near 0%.
4. In chokepoint jams verify `backpressure(active=...)` rises and `avgScale` drops while units still move toward center.
5. Profiler check: gameplay `GC Alloc` remains `0 B`.

## 2026-02-21 - Fix: Pressure buffer publish race (CopyFloatArrayJob safety)

### What changed
- Replaced direct main-thread `DynamicBuffer<PressureCell>` write scheduling with a dedicated publish job using `BufferLookup<PressureCell>`:
  - `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`
- Updated pressure consumers to fetch pressure buffer via read-only `BufferLookup<PressureCell>`:
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
  - `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`
- Added rare resize guard: if pressure grid cell count changes, buffer is resized once before publish.

### Why
- Prevent `InvalidOperationException` safety conflicts between scheduled pressure-buffer writer jobs and subsequent buffer access in later systems.

### How to test
1. Enter Play Mode and run with pressure enabled.
2. Verify no `CopyFloatArrayJob` / `PressureCell` safety exceptions occur.
3. Confirm steering + HordeTune still receive pressure data.
4. Profiler: gameplay loop remains `GC Alloc = 0 B`.

## 2026-02-21 - Fix: remove PressureCell main-thread access + keep publish fully scheduled

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - removed `state.Dependency.Complete()` from pressure publish path.
  - moved `PressureCell` buffer resize into `PublishPressureToBufferJob` so resize+copy happen in one scheduled writer job.
  - added `[UpdateBefore(typeof(HordeTuningQuickMetricsSystem))]` to make ordering explicit.
- Updated `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`:
  - removed main-thread pressure buffer indexing.
  - metrics now read pressure only inside `EvaluateQuickMetricsJob` via read-only `BufferLookup<PressureCell>`.
- Updated `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`:
  - removed main-thread pressure buffer indexing.
  - steering now reads pressure only inside `ZombieSteeringJob` via read-only `BufferLookup<PressureCell>`.
- Updated docs:
  - `Docs/Systems/Horde/HordePressureField.md`
  - `Docs/Systems/Horde/HordeTuningQuickMetrics.md`
  - `Docs/Systems/Horde/ZombieSteering.md`

### Why
- Eliminates `InvalidOperationException` caused by touching `PressureCell` from main thread while publish job writes were still in-flight.
- Keeps the entire pressure write/read path dependency-chained and Burst/job-friendly without sync points.

### How to test
1. Open `Assets/Scenes/SampleScene.unity` and enter Play Mode.
2. Run with pressure enabled and verify no `InvalidOperationException` mentioning `CopyFloatArrayJob` / `PressureCell`.
3. Confirm steering still reacts to congestion and `[HordeTune]` logs still include backpressure metrics.
4. Profiler watchlist:
   - `GC Alloc` remains `0 B` in gameplay.
   - no new main-thread sync spikes from `Complete()`.

## 2026-02-21 - Fix: main-thread buffer resize + job-only publish writes

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - `DynamicBuffer<PressureCell>.ResizeUninitialized` is now done on main thread before scheduling publish job.
  - `PublishPressureToBufferJob` no longer changes buffer length/capacity; it only copies values.
- Kept pressure consumers job-side only via read-only `BufferLookup<PressureCell>`:
  - `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`
  - `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`
- Updated pressure system doc:
  - `Docs/Systems/Horde/HordePressureField.md`

### Why
- Enforces ECS safety pattern where dynamic buffer structural size changes happen on main thread, while content copy remains scheduled.
- Keeps dependency chaining intact and avoids introducing any `Complete()` sync points.

### How to test
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
2. Verify no `InvalidOperationException` involving `CopyFloatArrayJob` / `PressureCell`.
3. Confirm pressure still affects steering and quick metrics still read pressure.
4. Profiler watchlist:
   - gameplay `GC Alloc = 0 B`
   - no new `Complete()` sync spikes.

## 2026-02-21 - ECS perf warnings cleanup (cached queries/lookups) + metrics OOB hardening

### What changed
- Updated `Assets/_Project/Scripts/Map/WallFieldBuildSystem.cs`:
  - moved map/wall singleton `EntityQuery` creation to `OnCreate`.
  - `OnUpdate` now reuses cached queries (no per-frame query creation).
- Updated `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`:
  - moved `BufferLookup<PressureCell>` creation to `OnCreate`.
  - `OnUpdate` now calls `_pressureLookup.Update(ref state)` and reuses cached lookup.
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - moved writable `BufferLookup<PressureCell>` creation to `OnCreate`.
  - `OnUpdate` now calls `_pressureLookup.Update(ref state)` and reuses cached lookup for publish job.
  - cached pressure-buffer singleton query and reused it in `OnUpdate`.
- Updated `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`:
  - moved read-only `BufferLookup<PressureCell>` creation to `OnCreate` and refresh with `.Update(ref state)` in `OnUpdate`.
  - hardened per-thread counter capacity validation to recreate arrays if thread count or array lengths drift.
  - `ClearThreadCountersJob` uses actual array length and includes histogram bounds guard.
- Updated docs:
  - `Docs/Systems/Horde/ZombieSteering.md`
  - `Docs/Systems/Horde/HordePressureField.md`
  - `Docs/Systems/Horde/HordeTuningQuickMetrics.md`
  - `Docs/Systems/Map/MapGenerator.md`

### Why
- Removes Entities performance warnings about creating queries/lookups in `OnUpdate`.
- Prevents Burst-side out-of-range in `ClearThreadCountersJob` when thread-counter array sizes desync.
- Keeps scheduling fully dependency-chained with zero `Complete()` sync points.

### How to test
1. Enter Play Mode in `Assets/Scenes/SampleScene.unity`.
2. Verify no warnings about:
   - `WallFieldBuildSystem` creating query in `OnUpdate`.
   - `ZombieSteeringSystem` / `HordePressureFieldSystem` creating lookup in `OnUpdate`.
3. Verify no Burst `IndexOutOfRangeException` in `ClearThreadCountersJob`.
4. Profiler watchlist:
   - gameplay `GC Alloc = 0 B`
   - no new main-thread sync spikes.

## 2026-02-21 - Metrics safety fix: remove PressureCell buffer access from Burst parallel job

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`:
  - removed `DynamicBuffer<PressureCell>` / `BufferLookup<PressureCell>` access from `EvaluateQuickMetricsJob`.
  - backpressure metrics now use a density proxy computed from sampled local neighbors:
    - `density = 1 + localNeighbors`
    - `pressureProxy = max(0, density - TargetUnitsPerCell)`
    - `excess = max(0, pressureProxy - BackpressureThreshold)`
  - split counter clearing into two jobs:
    - `ClearScalarCountersJob` (`IJobParallelFor`) for scalar/thread counters.
    - `ClearHistogramJob` (`IJobParallelFor`) for flat histogram clear.
  - kept reduction in `ReduceQuickMetricsJob` with `WorkerCount * HistogramBins` source length and `ReducedHistogram` bin count.
  - updated runtime log format to label backpressure as density-proxy:
    - `backpressure(densityProxyThreshold=..., k=..., active=...%)`
- Updated docs:
  - `Docs/Systems/Horde/HordeTuningQuickMetrics.md`

### Why
- Avoids Burst safety exceptions (`ReadWriteBuffers are restricted...`) caused by dynamic buffer access patterns in parallel metrics jobs.
- Keeps metrics parallel, Burst-compatible, allocation-free in hot path, and without adding `Complete()` sync points.

### How to test
1. Enter Play Mode with metrics enabled.
2. Verify no Burst abort / `ReadWriteBuffers` exception in `HordeTuningQuickMetricsSystem`.
3. Confirm `[HordeTune]` logs still print speed stats (`avg/p50/p90`) and backpressure (`active%`, `avgScale`, `minScale`) with `densityProxyThreshold`.

## 2026-02-21 - Pressure-only backpressure path + metrics pressure snapshot

### What changed
- Updated `Assets/_Project/Scripts/Horde/ZombieComponents.cs`:
  - added `ZombieGoalIntent` component (`Direction`, `StepDistance`) to separate goal intent from movement integration.
- Updated `Assets/_Project/Scripts/Horde/ZombieAuthoring.cs`:
  - baker now adds `ZombieGoalIntent` to zombie entities.
- Updated `Assets/_Project/Scripts/Horde/ZombieSteeringSystem.cs`:
  - `ZombieSteeringSystem` now computes only flow/center goal intent (no `LocalTransform` writes).
  - added `HordeBackpressureSystem` (scheduled after `HordePressureFieldSystem`) that:
    - reads `PressureCell` at current flow cell,
    - computes backpressure scale (`threshold/k/min/max`),
    - applies scaled goal-intent movement to `LocalTransform`.
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - raised default `BackpressureThreshold` to `2.0` to keep open-space speed near unscaled baseline.
- Updated `Assets/_Project/Scripts/Horde/HordeTuningQuickMetricsSystem.cs`:
  - added persistent `_pressureSnapshot` (`NativeArray<float>`).
  - added cached read-only `BufferLookup<PressureCell>` + cached pressure-buffer query in `OnCreate`.
  - added `CopyPressureSnapshotJob : IJob` (single-thread) to copy `PressureCell` buffer into snapshot each metrics tick.
  - `EvaluateQuickMetricsJob` no longer touches `DynamicBuffer`; it samples pressure from snapshot only.
  - logs now label backpressure as `pressureThreshold=...` (not density proxy).
- Updated docs:
  - `Docs/Systems/Horde/ZombieSteering.md`
  - `Docs/Systems/Horde/HordePressureField.md`
  - `Docs/Systems/Horde/HordeTuningQuickMetrics.md`

### Why
- Keeps backpressure driven only by published pressure-field values.
- Removes Burst parallel `DynamicBuffer` access from metrics job to eliminate `ReadWriteBuffers` restriction crashes.
- Preserves jobified dependency chaining with zero hot-path `Complete()` stalls.

### How to test
1. Enter Play Mode with pressure and quick metrics enabled.
2. Verify no Burst abort / `ReadWriteBuffers` exception and no `InvalidOperationException` around `PressureCell`.
3. Confirm `[HordeTune]` still prints speed stats (`avg/p50/p90`) and `backpressure(... active=... avgScale=... minScale=...)`.
4. In open space, verify backpressure active% stays low; in chokepoints, verify active% rises.
5. Profiler watchlist:
   - gameplay `GC Alloc = 0 B`
   - no new sync spikes from `Complete()`.

## 2026-02-21 - Tuning iteration: reduce small-crowd slowdown, keep sink handling

### What changed
- Updated pressure/backpressure defaults in `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - `BackpressureThreshold: 2.0 -> 3.0`
  - `BackpressureK: 0.35 -> 0.20`
  - `MinSpeedFactor: 0.20 -> 0.30`
- Updated separation defaults in `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - `MaxNeighbors: 24 -> 32`
  - `MaxPushPerFrame: 0.75 -> 0.90`

### Why
- Run data showed early slowdown in smaller/non-jammed samples (`jam=0` while backpressure became active and avgScale dropped), so backpressure engagement was too aggressive.
- Late-run sink stress also showed high overlap, so soft separation authority was raised moderately without changing algorithm/order.

### How to test
1. Run same scenario and compare `[HordeTune]` windows before/after.
2. Confirm free/small-crowd windows keep `backpressure active` near `0..5%`.
3. Confirm average speed and `avgScale` remain closer to `1.0` before hard congestion.
4. In sink stress, verify overlap/jam trend improves or at least does not worsen while no new sync points appear.

## 2026-02-21 - Tuning iteration: sink-jam mitigation (overlap+jam climb)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - `PressureStrength: 0.50 -> 0.60`
  - `SpeedFractionCap: 0.25 -> 0.30`
  - `BackpressureThreshold: 3.0 -> 3.5`
  - `BackpressureK: 0.20 -> 0.30`
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - `MaxPushPerFrame: 0.90 -> 1.10`

### Why
- Open-space behavior was already healthy (`backpressure active ~0%`), but sink stress still escalated into high overlap/jam with saturated low speed.
- This pass increases macro pressure relief + soft separation authority while keeping backpressure less eager in moderate congestion and still strong in high pressure.

### How to test
1. Re-run same sink stress scenario and compare `[HordeTune]` trends over time.
2. Confirm early/open windows still keep `backpressure active` near `0..5%`.
3. Check if late-run overlap/jam escalation slows down (or peaks lower) versus previous run.
4. Verify no new sync points and `GC Alloc = 0 B` in gameplay.

## 2026-02-21 - Tuning iteration: sink-jam mitigation pass 2 (threshold split + stronger soft push)

### What changed
- Updated `Assets/_Project/Scripts/Horde/HordePressureFieldSystem.cs`:
  - `TargetUnitsPerCell: 2.2 -> 2.0`
  - `BackpressureThreshold: 3.5 -> 4.0`
  - `BackpressureK: 0.30 -> 0.40`
- Updated `Assets/_Project/Scripts/Horde/HordeSeparationSystem.cs`:
  - `MaxPushPerFrame: 1.10 -> 1.30`

### Why
- Run still climbed from intermittent overlap (`14.3%`) to high overlap/jam (`100%/100%`) over sink stress windows.
- Backpressure was active in moderate density windows (`42.9%`, then `57.1%+`), so threshold was raised to keep open and mid-density motion freer while increasing high-pressure braking slope.
- Lower `TargetUnitsPerCell` engages pressure relief earlier, and higher separation cap increases local de-overlap authority at chokepoints.

### How to test
1. Run the same sink stress scenario and compare `[HordeTune]` trend windows over time.
2. Confirm early and mid windows keep `backpressure active` low while `avgScale` stays closer to `1.0`.
3. Check whether overlap and jam escalate slower and peak lower than the previous run.
4. Verify no new sync points and `GC Alloc = 0 B` in gameplay.
