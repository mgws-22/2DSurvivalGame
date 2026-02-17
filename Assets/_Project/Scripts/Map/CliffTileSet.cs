using UnityEngine;
using UnityEngine.Tilemaps;

namespace Project.Map
{
    [CreateAssetMenu(menuName = "Project/Map/Cliff Tile Set", fileName = "CliffTileSet")]
    public sealed class CliffTileSet : ScriptableObject
    {
        [SerializeField] private TileBase _groundTile;
        [SerializeField] private TileBase[] _cliffMaskTiles = new TileBase[16];

        public TileBase GroundTile => _groundTile;

        public TileBase GetCliffTile(int mask)
        {
            if (_cliffMaskTiles == null || _cliffMaskTiles.Length < 16)
            {
                return null;
            }

            return _cliffMaskTiles[mask & 0xF];
        }

        public bool HasGroundTile()
        {
            return _groundTile != null;
        }

#if UNITY_EDITOR
        public void SetTiles(TileBase groundTile, TileBase[] cliffMaskTiles)
        {
            _groundTile = groundTile;

            if (_cliffMaskTiles == null || _cliffMaskTiles.Length != 16)
            {
                _cliffMaskTiles = new TileBase[16];
            }

            if (cliffMaskTiles == null)
            {
                for (int i = 0; i < 16; i++)
                {
                    _cliffMaskTiles[i] = null;
                }

                return;
            }

            for (int i = 0; i < 16; i++)
            {
                _cliffMaskTiles[i] = i < cliffMaskTiles.Length ? cliffMaskTiles[i] : null;
            }
        }
#endif
    }
}
