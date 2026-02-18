using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;

namespace Project.Map
{
    public struct MapRuntimeData : IComponentData
    {
        public int Width;
        public int Height;
        public int SpawnMargin;
        public int CenterOpenRadius;
        public float TileSize;
        public float2 Origin;
        public float2 CenterWorld;

        public float3 GridToWorld(int2 grid, float z)
        {
            float2 world = Origin + ((new float2(grid.x + 0.5f, grid.y + 0.5f)) * TileSize);
            return new float3(world.x, world.y, z);
        }

        public int2 WorldToGrid(float2 world)
        {
            float2 local = (world - Origin) / TileSize;
            return (int2)math.floor(local);
        }

        public bool IsInMap(int2 grid)
        {
            return grid.x >= 0 && grid.y >= 0 && grid.x < Width && grid.y < Height;
        }

        public int ToIndex(int2 grid)
        {
            return grid.x + (grid.y * Width);
        }
    }

    public struct MapWalkableCell : IBufferElementData
    {
        public byte Value;

        public bool IsWalkable => Value != 0;
    }

    public static class MapEcsBridge
    {
        public static bool Sync(MapData mapData)
        {
            if (mapData == null)
            {
                return false;
            }
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            EntityManager entityManager = world.EntityManager;
            EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadWrite<MapRuntimeData>(),
                ComponentType.ReadWrite<MapWalkableCell>());

            Entity mapEntity;
            if (query.IsEmptyIgnoreFilter)
            {
                mapEntity = entityManager.CreateEntity(typeof(MapRuntimeData), typeof(MapWalkableCell));
            }
            else
            {
                mapEntity = query.GetSingletonEntity();
            }

            query.Dispose();

            float2 centerWorld = mapData.WorldOrigin +
                (new float2(mapData.Width * mapData.TileSize, mapData.Height * mapData.TileSize) * 0.5f);

            MapRuntimeData runtimeData = new MapRuntimeData
            {
                Width = mapData.Width,
                Height = mapData.Height,
                SpawnMargin = mapData.SpawnMargin,
                CenterOpenRadius = mapData.CenterOpenRadius,
                TileSize = mapData.TileSize,
                Origin = mapData.WorldOrigin,
                CenterWorld = centerWorld
            };

            entityManager.SetComponentData(mapEntity, runtimeData);

            DynamicBuffer<MapWalkableCell> walkable = entityManager.GetBuffer<MapWalkableCell>(mapEntity);
            int tileCount = mapData.TileCount;
            walkable.ResizeUninitialized(tileCount);

            for (int i = 0; i < tileCount; i++)
            {
                int2 grid = mapData.IndexToGrid(i);
                walkable[i] = new MapWalkableCell
                {
                    Value = mapData.IsWalkable(grid.x, grid.y) ? (byte)1 : (byte)0
                };
            }

            if (!entityManager.HasComponent<FlowFieldDirtyTag>(mapEntity))
            {
                entityManager.AddComponent<FlowFieldDirtyTag>(mapEntity);
            }

            if (!entityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
            {
                entityManager.AddComponent<WallFieldDirtyTag>(mapEntity);
            }

            return true;
        }
    }
}
