using UnityEngine;
using UnityEngine.Tilemaps;

namespace Project.Map
{
    [DisallowMultipleComponent]
    public sealed class MapTilemapRenderer : MonoBehaviour
    {
        [SerializeField] private Tilemap _tilemap;
        [SerializeField] private TileBase _groundTile;
        [SerializeField] private TileBase _cliffTile;

        [Header("Runtime Tile Fallback")]
        [SerializeField] private Color _groundColor = new Color(0.28f, 0.42f, 0.2f);
        [SerializeField] private Color _cliffColor = new Color(0.2f, 0.2f, 0.22f);

        private TileBase[] _tileBuffer;

        private Tile _runtimeGroundTile;
        private Tile _runtimeCliffTile;

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

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = rowOffset + x;
                    _tileBuffer[index] = mapData.IsWalkable(x, y) ? _groundTile : _cliffTile;
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
            if (_groundTile == null)
            {
                _runtimeGroundTile = CreateRuntimeTile(_groundColor, "RuntimeGroundTile");
                _groundTile = _runtimeGroundTile;
            }

            if (_cliffTile == null)
            {
                _runtimeCliffTile = CreateRuntimeTile(_cliffColor, "RuntimeCliffTile");
                _cliffTile = _runtimeCliffTile;
            }
        }

        private static Tile CreateRuntimeTile(Color color, string tileName)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = tileName + "_Texture"
            };

            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            sprite.name = tileName + "_Sprite";

            Tile tile = ScriptableObject.CreateInstance<Tile>();
            tile.name = tileName;
            tile.sprite = sprite;
            tile.colliderType = Tile.ColliderType.None;
            return tile;
        }
    }
}
