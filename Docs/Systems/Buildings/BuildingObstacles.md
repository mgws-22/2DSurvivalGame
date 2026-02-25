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

Runtime systems:
- `BuildingObstacleStampSystem` (`Assets/_Project/Scripts/Buildings/BuildingObstacleStampSystem.cs`)
- `WallFieldBuildSystem` consumes `DynamicObstacleRect` during rebuild (`Assets/_Project/Scripts/Map/WallFieldBuildSystem.cs`)
- `WallRepulsionSystem` treats `wallDist == 0` cells as blocked (`Assets/_Project/Scripts/Horde/WallRepulsionSystem.cs`)

Authoring / editor:
- `WallBuildingAuthoring` (`Assets/_Project/Scripts/Buildings/WallBuildingAuthoring.cs`)
- `Tools/Buildings/Create Wall Prefab` (`Assets/_Project/Editor/Tools/BuildingPrefabTool.cs`)
- `Tools/Buildings/Create/Ensure StaticBuildings SubScene` (`Assets/_Project/Scripts/Editor/Buildings/WallSubSceneTools.cs`)
- `Tools/Buildings/Move Selected Walls To StaticBuildings SubScene` (`Assets/_Project/Scripts/Editor/Buildings/WallSubSceneTools.cs`)
- `Tools/Buildings/Validate Wall Baking` (`Assets/_Project/Scripts/Editor/Buildings/WallSubSceneTools.cs`)

## Runtime Logic
1. A wall building prefab bakes `BuildingTag` + `BuildingFootprint`.
2. `BuildingObstacleStampSystem` finds unstamped building entities (`without ObstacleStampedTag`).
3. It converts building world position to map-cell space (`MapRuntimeData.WorldToGrid`).
4. It computes a footprint rectangle in map-cell space using `SizeCells` + `PivotOffsetCells`.
5. It appends the rectangle to the singleton `DynamicObstacleRect` buffer.
6. It marks the building with `ObstacleStampedTag` so stamping happens only once per entity.
7. If any building was stamped, it adds `WallFieldDirtyTag` to the map entity.
8. `WallFieldBuildSystem` rebuilds the wall field and treats dynamic obstacle rectangles as blocked seeds (`wallDist == 0`) in addition to static blocked map tiles.
9. `WallRepulsionSystem` uses the wall field to repel/project zombies out of obstacle cells, but flow-field steering remains unchanged (no reroute).

## Invariants
- Buildings added by this pipeline block zombies through wall repulsion only.
- Static scene wall GameObjects must be inside a SubScene to bake into `BuildingTag` entities.
- Flow field pathfinding is unchanged; zombies may jam against buildings instead of pathing around them.
- `BuildingObstacleStampSystem` dirties only `WallFieldDirtyTag` (never `FlowFieldDirtyTag`).
- Dynamic obstacle rectangles are stored in map-cell space.
- Wall repulsion hot path does not scan the obstacle rectangle list per zombie.
- No Unity Physics colliders or rigidbodies are required.

## Static Scene Wall Workflow
1. Place or select wall GameObjects in the main scene hierarchy.
2. Run `Tools/Buildings/Move Selected Walls To StaticBuildings SubScene`.
3. The tool creates/ensures `StaticBuildingsSubScene` and moves the selected wall GameObjects into its editing scene.
4. In Play Mode, baked wall entities should appear in the Default World as `BuildingTag` and be picked up by `BuildingObstacleStampSystem`.

## Performance
- Stamping is intended to be rare (on building spawn/creation), not a per-frame hot path.
- `BuildingObstacleStampSystem` performs structural changes only for newly stamped buildings (`ObstacleStampedTag`).
- `WallFieldBuildSystem` checks dynamic rectangles only during wall-field rebuild, not per zombie.
- `WallRepulsionSystem` remains Burst/job-friendly and allocation-free per frame.

## Verification
1. Create `Wall.prefab` via `Tools/Buildings/Create Wall Prefab`.
2. Place the prefab inside a corridor toward the center.
3. Enter Play Mode and verify zombies keep steering toward center but jam against the wall instead of rerouting.
4. Force zombies onto the wall footprint and verify they are projected out (cannot remain inside).
5. Profile and confirm `GC Alloc = 0 B` in the play loop and no new sync-point spikes.
