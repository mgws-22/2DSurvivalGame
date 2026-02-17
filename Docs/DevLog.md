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
