using Unity.Mathematics;
using UnityEngine;
using System;

namespace Project.Map
{
    public sealed class MapData
    {
        private readonly TileType[] _tileType;
        private readonly bool[] _walkable;
        private readonly int2[] _gateCenters;

        public int Width { get; }
        public int Height { get; }
        public float TileSize { get; }
        public int SpawnMargin { get; }
        public int CenterOpenRadius { get; }

        public float2 WorldOrigin { get; }

        public int TileCount => _tileType.Length;
        public int GateCount => _gateCenters.Length;

        public MapData(int width, int height, float tileSize, int spawnMargin, int centerOpenRadius, float2 worldOrigin, int2[] gateCenters)
        {
            Width = width;
            Height = height;
            TileSize = tileSize;
            SpawnMargin = spawnMargin;
            CenterOpenRadius = centerOpenRadius;
            WorldOrigin = worldOrigin;

            _tileType = new TileType[width * height];
            _walkable = new bool[width * height];
            _gateCenters = gateCenters ?? Array.Empty<int2>();
        }

        public bool IsInMap(int2 grid)
        {
            return grid.x >= 0 && grid.y >= 0 && grid.x < Width && grid.y < Height;
        }

        public int Index(int2 grid)
        {
            return grid.x + (grid.y * Width);
        }

        public int2 IndexToGrid(int index)
        {
            int y = index / Width;
            int x = index - (y * Width);
            return new int2(x, y);
        }

        public TileType GetTileType(int x, int y)
        {
            return _tileType[x + (y * Width)];
        }

        public bool IsWalkable(int x, int y)
        {
            return _walkable[x + (y * Width)];
        }

        public bool IsWalkable(int2 grid)
        {
            return IsInMap(grid) && _walkable[Index(grid)];
        }

        public int2 GetGateCenter(int index)
        {
            return _gateCenters[index];
        }

        public float2 GridToWorld(int2 grid)
        {
            return WorldOrigin + (new float2(grid.x + 0.5f, grid.y + 0.5f) * TileSize);
        }

        public Bounds GetPlayAreaBoundsWorld()
        {
            float2 size2D = new float2(Width * TileSize, Height * TileSize);
            float2 center2D = WorldOrigin + (size2D * 0.5f);
            return new Bounds(new Vector3(center2D.x, center2D.y, 0f), new Vector3(size2D.x, size2D.y, 0.01f));
        }

        public Bounds GetSpawnAreaBoundsWorld()
        {
            float marginWorld = SpawnMargin * TileSize;
            float2 size2D = new float2((Width * TileSize) + (marginWorld * 2f), (Height * TileSize) + (marginWorld * 2f));
            float2 center2D = WorldOrigin + (new float2(Width * TileSize, Height * TileSize) * 0.5f);
            return new Bounds(new Vector3(center2D.x, center2D.y, 0f), new Vector3(size2D.x, size2D.y, 0.01f));
        }

        internal void FillFromWalkable(bool[] walkable)
        {
            for (int i = 0; i < _walkable.Length; i++)
            {
                bool value = walkable[i];
                _walkable[i] = value;
                _tileType[i] = value ? TileType.Ground : TileType.Cliff;
            }
        }
    }
}
