# Building Obstacles (Dynamic WallField Stamps)

## Purpose
Provide a shared ECS foundation for building prefabs that should physically block zombies using the existing `WallFieldBuildSystem` + `WallRepulsionSystem`, without changing flow-field pathfinding.

Current first type:
- `Wall` building prefab (no attack behavior in this task)

## Data
- `BuildingTag` (`Assets/_Project/Scripts/Buildings/BuildingObstacleComponents.cs`)
- `BuildingFootprint` (`Assets/_Project/Scripts/Buildings/BuildingObstacleComponents.cs`)
- `ObstacleStampedTag` (`Assets/_Project/Scripts/Buildings/BuildingObstacleComponents.cs`)
- `DynamicObstacleRect` buffer (`Assets/_Project/Scripts/Buildings/BuildingObstacleComponents.cs`)
- `DynamicObstacleRegistryTag` singleton marker (`Assets/_Project/Scripts/Buildings/BuildingObstacleComponents.cs`)
- `BuildingPrefabCatalog` singleton (`Assets/_Project/Scripts/Buildings/Placement/BuildingPlacementComponents.cs`)
- `PlaceBuildingRequest` buffer (`Assets/_Project/Scripts/Buildings/Placement/BuildingPlacementComponents.cs`)
- `PlaceBuildingRequestQueueTag` singleton marker (`Assets/_Project/Scripts/Buildings/Placement/BuildingPlacementComponents.cs`)

Runtime systems:
- `BuildingPlacementSystem` (`Assets/_Project/Scripts/Buildings/Placement/BuildingPlacementSystem.cs`)
- `BuildingObstacleStampSystem` (`Assets/_Project/Scripts/Buildings/BuildingObstacleStampSystem.cs`)
- `WallFieldBuildSystem` consumes `DynamicObstacleRect` during rebuild (`Assets/_Project/Scripts/Map/WallFieldBuildSystem.cs`)
- `WallRepulsionSystem` treats `wallDist == 0` cells as blocked (`Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`)

Authoring / editor:
- `WallBuildingAuthoring` (`Assets/_Project/Scripts/Buildings/WallBuildingAuthoring.cs`)
- `BuildingPrefabCatalogAuthoring` (`Assets/_Project/Scripts/Buildings/Placement/BuildingPrefabCatalogAuthoring.cs`)
- `Tools/Buildings/Setup Build HUD (SampleScene)` (`Assets/_Project/Editor/Tools/BuildHudSetupTool.cs`)
- `Tools/Buildings/Create Wall Prefab` (`Assets/_Project/Editor/Tools/BuildingPrefabTool.cs`)
- `Tools/Buildings/Create/Ensure StaticBuildings SubScene` (`Assets/_Project/Scripts/Editor/Buildings/WallSubSceneTools.cs`)
- `Tools/Buildings/Move Selected Walls To StaticBuildings SubScene` (`Assets/_Project/Scripts/Editor/Buildings/WallSubSceneTools.cs`)
- `Tools/Buildings/Validate Wall Baking` (`Assets/_Project/Scripts/Editor/Buildings/WallSubSceneTools.cs`)

Runtime UI / input:
- `BuildMenuController` (`Assets/_Project/Scripts/UI/BuildMenu/BuildMenuController.cs`)
- `BuildingPlacementController` (`Assets/_Project/Scripts/Buildings/Placement/BuildingPlacementController.cs`)

## Runtime Logic
1. `BuildMenuController` exposes a simple 4x4 bottom-left HUD and enters wall placement mode.
2. `BuildingPlacementController` snaps the mouse to one map cell (`MapRuntimeData.WorldToGrid` / `GridToWorld`) and shows a ghost preview.
3. `BuildingPlacementController` computes per-frame placement validity and UI feedback:
   - manual UI raycast hit test (allocation-free cached `PointerEventData` + `List<RaycastResult>`)
   - ghost tint (green valid / red invalid)
   - status text reason (`Cannot place: ...`)
4. On LMB, it validates placement (in bounds, base walkable `MapWalkableCell`, not over UI, not already inside any `DynamicObstacleRect`) and appends `PlaceBuildingRequest`.
5. `BuildingPrefabCatalogAuthoring` also runtime-syncs the catalog singleton in Play Mode so the wall prefab entity reference exists even without relying on main-scene baking.
6. `BuildingPlacementSystem` consumes placement requests and instantiates the wall prefab entity from `BuildingPrefabCatalog`.
7. A wall building prefab bakes `BuildingTag` + `BuildingFootprint`.
8. `BuildingObstacleStampSystem` finds unstamped building entities (`without ObstacleStampedTag`).
9. It converts building world position to map-cell space (`MapRuntimeData.WorldToGrid`).
10. It computes a footprint rectangle in map-cell space using `SizeCells` + `PivotOffsetCells`.
11. It appends the rectangle to the singleton `DynamicObstacleRect` buffer.
12. It marks the building with `ObstacleStampedTag` so stamping happens only once per entity.
13. If any building was stamped, it adds `WallFieldDirtyTag` to the map entity.
14. `WallFieldBuildSystem` schedules an async rebuild job and keeps using the previous wall-field blob until the new one is ready.
15. During rebuild scheduling, dynamic obstacle rectangles are converted once into an expanded-grid occupancy mask (`O(rect area)` total) so per-tile obstacle checks are `O(1)`.
16. On job completion, `WallFieldBuildSystem` swaps in the new blob, updates `WallFieldStats`, and clears `WallFieldDirtyTag` when the rebuilt snapshot still matches the current map/rect state.
17. `WallRepulsionSystem` uses the wall field to repel/project zombies out of obstacle cells, but flow-field steering remains unchanged (no reroute).

## Invariants
- Buildings added by this pipeline block zombies through wall repulsion only.
- Runtime placement never dirties `FlowFieldDirtyTag`; it only spawns wall entities and lets obstacle stamping dirty `WallFieldDirtyTag`.
- Static scene wall GameObjects must be inside a SubScene to bake into `BuildingTag` entities.
- Flow field pathfinding is unchanged; zombies may jam against buildings instead of pathing around them.
- `BuildingObstacleStampSystem` dirties only `WallFieldDirtyTag` (never `FlowFieldDirtyTag`).
- Dynamic obstacle rectangles are stored in map-cell space.
- Wall repulsion hot path does not scan the obstacle rectangle list per zombie.
- Runtime placement checks `DynamicObstacleRect` only on click to prevent stacking walls on the same cell.
- Placement status feedback uses fixed reason strings (no per-frame string building).
- `BuildingObstacleStampSystem` appends one `DynamicObstacleRect` per newly stamped building and should not grow when no new buildings are stamped.
- No Unity Physics colliders or rigidbodies are required.

## Static Scene Wall Workflow
1. Place or select wall GameObjects in the main scene hierarchy.
2. Run `Tools/Buildings/Move Selected Walls To StaticBuildings SubScene`.
3. The tool creates/ensures `StaticBuildingsSubScene` and moves the selected wall GameObjects into its editing scene.
4. In Play Mode, baked wall entities should appear in the Default World as `BuildingTag` and be picked up by `BuildingObstacleStampSystem`.

## Performance
- Stamping is intended to be rare (on building spawn/creation), not a per-frame hot path.
- Placement validation scans the dynamic obstacle rectangle buffer only on click (`O(rectCount)` per click), never per zombie/per frame.
- Build HUD is event-driven; placement mode per-frame work is limited to mouse-to-grid snap, manual UI raycast, validity checks, and ghost/status updates.
- UI raycast hit testing reuses cached containers (`PointerEventData`, static `List<RaycastResult>`) to avoid per-frame allocations.
- `BuildingObstacleStampSystem` performs structural changes only for newly stamped buildings (`ObstacleStampedTag`).
- `WallFieldBuildSystem` no longer performs a per-tile linear scan over all `DynamicObstacleRect`; it prebuilds an occupancy mask once per rebuild and uses `O(1)` blocked checks in the distance/gradient passes.
- `WallFieldBuildSystem` reuses persistent `NativeArray` buffers and runs the distance/gradient rebuild job asynchronously, reducing the placement-frame main-thread hitch.
- `WallRepulsionSystem` remains Burst/job-friendly and allocation-free per frame.

## Verification
1. Run `Tools/Buildings/Setup Build HUD (SampleScene)` to create the HUD, EventSystem, and `BuildingPrefabCatalogAuthoring`.
2. Enter Play Mode and confirm the bottom-left 4x4 grid shows only `Deffens Buildings` in the bottom-left slot.
3. Click `Deffens Buildings`, then click `Wall`, and verify the placement ghost snaps to single map cells.
4. LMB place multiple walls and confirm `c:BuildingTag` entity count increases in Entities Hierarchy.
5. Verify zombies jam against the placed walls (wall repulsion) and do not reroute around them.
6. RMB cancel placement and confirm the ghost disappears and placement status clears.
7. In Profiler, confirm the wall placement frame no longer shows a large main-thread `WallFieldBuildSystem` spike; rebuild work should complete asynchronously and swap later.
8. Confirm `WallFieldStats.RebuildCount` increments on completed rebuilds (not every frame) and `DynamicObstacleRect` length tracks placed walls without creeping up when idle.
9. Profile and confirm `GC Alloc = 0 B` in the gameplay loop and no unexpected main-thread spikes/sync points.
