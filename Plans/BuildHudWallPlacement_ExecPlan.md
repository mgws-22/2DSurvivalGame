# BuildHudWallPlacement ExecPlan

## Goals
- Add a basic RTS-style build HUD (uGUI) with a root menu and a defense submenu.
- Add runtime wall placement input with grid snapping, ghost preview, and unlimited LMB placement until RMB cancel.
- Spawn real ECS wall entities through a request buffer + placement system so existing obstacle stamping/wall repulsion handles blocking.
- Keep flow-field pathfinding unchanged and hot gameplay loop allocation-free.

## Non-Goals
- Enemy path rerouting around placed walls.
- Physics colliders or rigidbodies for buildings.
- Advanced building validation (rotation, costs, adjacency, drag placement).

## Steps
1. Add ECS placement components (`BuildingPrefabCatalog`, request buffer types) and wall prefab catalog authoring baker.
2. Add `BuildingPlacementSystem` to consume placement requests and instantiate wall prefab entities before obstacle stamping.
3. Add runtime `BuildingPlacementController` (mouse -> snapped grid -> request append) and `BuildMenuController` (HUD state + wall placement trigger).
4. Add editor setup tool to build the HUD/EventSystem and ensure `BuildingPrefabCatalogAuthoring` in `SampleScene`.
5. Update building obstacle docs and dev log with runtime placement behavior, perf constraints, and verification steps.

## Perf Risks
- Placement validation scans `DynamicObstacleRect` on click (`O(rectCount)` per click) to prevent stacking; this is acceptable as a non-hot path.
- UI is event-driven; per-frame placement work is limited to mouse-to-grid snap + ghost transform updates.
- Placement system uses a small request buffer and local ECB playback; no jobs or per-frame heavy scans.

## Verification
- Enter Play Mode and confirm the 4x4 HUD states/labels match requirements.
- Place multiple walls and confirm ECS `BuildingTag` count increases.
- Confirm zombies jam against placed walls (wall repulsion) and do not reroute.
- Confirm `GC Alloc` remains `0 B` in gameplay loop.
