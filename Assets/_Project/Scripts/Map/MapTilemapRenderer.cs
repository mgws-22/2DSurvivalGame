using UnityEngine;
using UnityEngine.Tilemaps;

namespace Project.Map
{
    [DisallowMultipleComponent]
    public sealed class MapTilemapRenderer : MonoBehaviour
    {
        private const string DefaultTileSetResourcePath = "Map/CliffTileSet";
        private const int MaskCount = 16;

        [SerializeField] private Tilemap _tilemap;
        [SerializeField] private CliffTileSet _cliffTileSet;

        [Header("Runtime Tile Fallback")]
        [SerializeField] private int _runtimeTextureSize = 16;
        [SerializeField] private int _runtimeEdgeThickness = 3;
        [SerializeField] private Color _groundColor = new Color(0.28f, 0.42f, 0.2f);
        [SerializeField] private Color _groundAccentColor = new Color(0.34f, 0.5f, 0.25f);
        [SerializeField] private Color _cliffColor = new Color(0.2f, 0.2f, 0.22f);
        [SerializeField] private Color _cliffAccentColor = new Color(0.26f, 0.26f, 0.28f);
        [SerializeField] private Color _cliffEdgeColor = new Color(0.62f, 0.62f, 0.66f);

        private TileBase[] _tileBuffer;

        private Tile _runtimeGroundTile;
        private TileBase[] _runtimeCliffTiles;
        private bool _searchedDefaultTileSet;

        public void Render(MapData mapData)
        {
            if (mapData == null)
            {
                return;
            }

            EnsureTilemap();
            EnsureTiles();

            int width = mapData.Width;
            int height = mapData.Height;
            int tileCount = width * height;

            if (_tileBuffer == null || _tileBuffer.Length != tileCount)
            {
                _tileBuffer = new TileBase[tileCount];
            }

            TileBase groundTile = ResolveGroundTile();
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = rowOffset + x;
                    if (mapData.IsWalkable(x, y))
                    {
                        _tileBuffer[index] = groundTile;
                        continue;
                    }

                    int mask = ComputeCliffMask(mapData, x, y);
                    _tileBuffer[index] = ResolveCliffTile(mask);
                }
            }

            _tilemap.ClearAllTiles();
            _tilemap.SetTilesBlock(new BoundsInt(0, 0, 0, width, height, 1), _tileBuffer);
            _tilemap.RefreshAllTiles();
        }

        public void EnsureTilemap()
        {
            if (_tilemap != null)
            {
                return;
            }

            Grid grid = GetComponent<Grid>();
            if (grid == null)
            {
                grid = gameObject.AddComponent<Grid>();
            }

            Transform tilemapTransform = transform.Find("PlayAreaTilemap");
            if (tilemapTransform == null)
            {
                GameObject tilemapObject = new GameObject("PlayAreaTilemap");
                tilemapObject.transform.SetParent(transform, false);
                _tilemap = tilemapObject.AddComponent<Tilemap>();
                tilemapObject.AddComponent<TilemapRenderer>();
            }
            else
            {
                _tilemap = tilemapTransform.GetComponent<Tilemap>();
                if (_tilemap == null)
                {
                    _tilemap = tilemapTransform.gameObject.AddComponent<Tilemap>();
                }

                if (tilemapTransform.GetComponent<TilemapRenderer>() == null)
                {
                    tilemapTransform.gameObject.AddComponent<TilemapRenderer>();
                }
            }
        }

        public void SetCellSize(float tileSize)
        {
            Grid grid = GetComponent<Grid>();
            if (grid == null)
            {
                grid = gameObject.AddComponent<Grid>();
            }

            grid.cellSize = new Vector3(tileSize, tileSize, 1f);
        }

        private void EnsureTiles()
        {
            int textureSize = Mathf.Max(2, _runtimeTextureSize);
            int edgeThickness = Mathf.Clamp(_runtimeEdgeThickness, 1, Mathf.Max(1, textureSize / 2));

            if (_cliffTileSet == null && !_searchedDefaultTileSet)
            {
                _searchedDefaultTileSet = true;
                _cliffTileSet = Resources.Load<CliffTileSet>(DefaultTileSetResourcePath);
            }

            if (_runtimeGroundTile == null)
            {
                Texture2D groundTexture = CliffTileTextureFactory.CreateGroundTexture(
                    textureSize,
                    _groundColor,
                    _groundAccentColor,
                    "RuntimeGroundTexture");
                _runtimeGroundTile = CreateRuntimeTile(groundTexture, "RuntimeGroundTile");
            }

            if (_runtimeCliffTiles == null || _runtimeCliffTiles.Length != MaskCount)
            {
                _runtimeCliffTiles = new TileBase[MaskCount];
                for (int mask = 0; mask < MaskCount; mask++)
                {
                    Texture2D cliffTexture = CliffTileTextureFactory.CreateCliffMaskTexture(
                        mask,
                        textureSize,
                        edgeThickness,
                        _cliffColor,
                        _cliffEdgeColor,
                        _cliffAccentColor,
                        "RuntimeCliffMaskTexture_" + mask.ToString("X1"));
                    _runtimeCliffTiles[mask] = CreateRuntimeTile(cliffTexture, "RuntimeCliffMaskTile_" + mask.ToString("X1"));
                }
            }
        }

        private TileBase ResolveGroundTile()
        {
            if (_cliffTileSet != null && _cliffTileSet.HasGroundTile())
            {
                return _cliffTileSet.GroundTile;
            }

            return _runtimeGroundTile;
        }

        private TileBase ResolveCliffTile(int mask)
        {
            int safeMask = mask & 0xF;
            if (_cliffTileSet != null)
            {
                TileBase tile = _cliffTileSet.GetCliffTile(safeMask);
                if (tile != null)
                {
                    return tile;
                }

                TileBase defaultTile = _cliffTileSet.GetCliffTile(0);
                if (defaultTile != null)
                {
                    return defaultTile;
                }
            }

            TileBase runtimeTile = _runtimeCliffTiles[safeMask];
            return runtimeTile ?? _runtimeGroundTile;
        }

        private static int ComputeCliffMask(MapData mapData, int x, int y)
        {
            int mask = 0;
            if (IsGround(mapData, x, y + 1))
            {
                mask |= 0x1;
            }

            if (IsGround(mapData, x + 1, y))
            {
                mask |= 0x2;
            }

            if (IsGround(mapData, x, y - 1))
            {
                mask |= 0x4;
            }

            if (IsGround(mapData, x - 1, y))
            {
                mask |= 0x8;
            }

            return mask;
        }

        private static bool IsGround(MapData mapData, int x, int y)
        {
            return x >= 0 && y >= 0 && x < mapData.Width && y < mapData.Height && mapData.IsWalkable(x, y);
        }

        private static Tile CreateRuntimeTile(Texture2D texture, string tileName)
        {
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                texture.width);
            sprite.name = tileName + "_Sprite";

            Tile tile = ScriptableObject.CreateInstance<Tile>();
            tile.name = tileName;
            tile.sprite = sprite;
            tile.colliderType = Tile.ColliderType.None;
            return tile;
        }
    }
}
