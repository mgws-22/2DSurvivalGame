using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Map
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(Project.Horde.ZombieSteeringSystem))]
    public partial struct FlowFieldBuildSystem : ISystem
    {
        private const int InfDistance = int.MaxValue;
        private const byte NoneDirection = 255;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<MapWalkableCell>();
            state.RequireForUpdate<FlowFieldDirtyTag>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton(out FlowFieldSingleton singleton) && singleton.Blob.IsCreated)
            {
                singleton.Blob.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            Entity mapEntity = SystemAPI.GetSingletonEntity<MapRuntimeData>();
            if (!state.EntityManager.HasComponent<FlowFieldDirtyTag>(mapEntity))
            {
                return;
            }

            MapRuntimeData map = SystemAPI.GetComponent<MapRuntimeData>(mapEntity);
            DynamicBuffer<MapWalkableCell> walkableBuffer = SystemAPI.GetBuffer<MapWalkableCell>(mapEntity, true);
            int tileCount = map.Width * map.Height;
            if (tileCount <= 0 || walkableBuffer.Length != tileCount)
            {
                state.EntityManager.RemoveComponent<FlowFieldDirtyTag>(mapEntity);
                return;
            }

            DynamicBuffer<GatePoint> gates = SystemAPI.GetBuffer<GatePoint>(mapEntity, true);
            double buildStart = Time.realtimeSinceStartupAsDouble;

            NativeArray<int> dist = new NativeArray<int>(tileCount, Allocator.Temp);
            NativeArray<int> queue = new NativeArray<int>(tileCount, Allocator.Temp);
            NativeArray<byte> dir = new NativeArray<byte>(tileCount, Allocator.Temp);
            for (int i = 0; i < tileCount; i++)
            {
                dist[i] = InfDistance;
                dir[i] = NoneDirection;
            }

            int2 center = map.WorldToGrid(map.CenterWorld);
            int radius = math.max(0, map.CenterOpenRadius);
            int radiusSq = radius * radius;
            int head = 0;
            int tail = 0;

            int minY = math.max(0, center.y - radius);
            int maxY = math.min(map.Height - 1, center.y + radius);
            for (int y = minY; y <= maxY; y++)
            {
                int dy = y - center.y;
                int row = y * map.Width;
                int minX = math.max(0, center.x - radius);
                int maxX = math.min(map.Width - 1, center.x + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - center.x;
                    if ((dx * dx) + (dy * dy) > radiusSq)
                    {
                        continue;
                    }

                    int index = row + x;
                    if (!walkableBuffer[index].IsWalkable || dist[index] == 0)
                    {
                        continue;
                    }

                    dist[index] = 0;
                    queue[tail++] = index;
                }
            }

            if (tail == 0)
            {
                int fallback = map.ToIndex(new int2(math.clamp(center.x, 0, map.Width - 1), math.clamp(center.y, 0, map.Height - 1)));
                if (walkableBuffer[fallback].IsWalkable)
                {
                    dist[fallback] = 0;
                    queue[tail++] = fallback;
                }
                else
                {
                    for (int i = 0; i < tileCount; i++)
                    {
                        if (walkableBuffer[i].IsWalkable)
                        {
                            dist[i] = 0;
                            queue[tail++] = i;
                            break;
                        }
                    }
                }
            }

            while (head < tail)
            {
                int current = queue[head++];
                int y = current / map.Width;
                int x = current - (y * map.Width);
                int nextDistance = dist[current] + 1;

                VisitNeighbor(x, y + 1, nextDistance, map.Width, map.Height, walkableBuffer, dist, queue, ref tail);
                VisitNeighbor(x + 1, y, nextDistance, map.Width, map.Height, walkableBuffer, dist, queue, ref tail);
                VisitNeighbor(x, y - 1, nextDistance, map.Width, map.Height, walkableBuffer, dist, queue, ref tail);
                VisitNeighbor(x - 1, y, nextDistance, map.Width, map.Height, walkableBuffer, dist, queue, ref tail);
            }

            int reachableCount = 0;
            for (int y = 0; y < map.Height; y++)
            {
                int row = y * map.Width;
                for (int x = 0; x < map.Width; x++)
                {
                    int index = row + x;
                    if (!walkableBuffer[index].IsWalkable || dist[index] == InfDistance)
                    {
                        dir[index] = NoneDirection;
                        continue;
                    }

                    reachableCount++;
                    int bestDistance = dist[index];
                    byte bestDir = NoneDirection;

                    TryBestNeighbor(x, y + 1, 0, bestDistance, map.Width, map.Height, dist, ref bestDistance, ref bestDir);
                    TryBestNeighbor(x + 1, y, 1, bestDistance, map.Width, map.Height, dist, ref bestDistance, ref bestDir);
                    TryBestNeighbor(x, y - 1, 2, bestDistance, map.Width, map.Height, dist, ref bestDistance, ref bestDir);
                    TryBestNeighbor(x - 1, y, 3, bestDistance, map.Width, map.Height, dist, ref bestDistance, ref bestDir);

                    dir[index] = bestDir;
                }
            }

            BlobBuilder builder = new BlobBuilder(Allocator.Temp);
            ref FlowFieldBlob root = ref builder.ConstructRoot<FlowFieldBlob>();
            root.Width = map.Width;
            root.Height = map.Height;
            root.CellSize = map.TileSize;
            root.OriginWorld = map.Origin;

            BlobBuilderArray<byte> dirBlob = builder.Allocate(ref root.Dir, tileCount);
            BlobBuilderArray<ushort> distBlob = builder.Allocate(ref root.Dist, tileCount);
            for (int i = 0; i < tileCount; i++)
            {
                dirBlob[i] = dir[i];
                distBlob[i] = dist[i] == InfDistance ? ushort.MaxValue : (ushort)math.min(ushort.MaxValue, dist[i]);
            }

            BlobAssetReference<FlowFieldBlob> blob = builder.CreateBlobAssetReference<FlowFieldBlob>(Allocator.Persistent);
            builder.Dispose();

            Entity flowEntity = GetOrCreateFlowEntity(ref state);
            FlowFieldSingleton current = state.EntityManager.GetComponentData<FlowFieldSingleton>(flowEntity);
            if (current.Blob.IsCreated)
            {
                current.Blob.Dispose();
            }

            current.Blob = blob;
            state.EntityManager.SetComponentData(flowEntity, current);
            state.EntityManager.RemoveComponent<FlowFieldDirtyTag>(mapEntity);

            double buildMs = (Time.realtimeSinceStartupAsDouble - buildStart) * 1000.0;
            Debug.Log($"FlowField built {map.Width}x{map.Height} in {buildMs:F2} ms. Reachable={reachableCount} Gates={gates.Length}");
        }

        private static Entity GetOrCreateFlowEntity(ref SystemState state)
        {
            EntityQuery query = state.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<FlowFieldSingleton>());
            Entity entity;
            if (query.IsEmptyIgnoreFilter)
            {
                entity = state.EntityManager.CreateEntity(typeof(FlowFieldSingleton));
                state.EntityManager.SetComponentData(entity, new FlowFieldSingleton());
            }
            else
            {
                entity = query.GetSingletonEntity();
            }

            query.Dispose();
            return entity;
        }

        private static void VisitNeighbor(
            int x,
            int y,
            int nextDistance,
            int width,
            int height,
            DynamicBuffer<MapWalkableCell> walkable,
            NativeArray<int> dist,
            NativeArray<int> queue,
            ref int tail)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            int index = x + (y * width);
            if (!walkable[index].IsWalkable || nextDistance >= dist[index])
            {
                return;
            }

            dist[index] = nextDistance;
            queue[tail++] = index;
        }

        private static void TryBestNeighbor(
            int x,
            int y,
            byte dirCode,
            int currentBest,
            int width,
            int height,
            NativeArray<int> dist,
            ref int bestDistance,
            ref byte bestDir)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            int neighborDistance = dist[x + (y * width)];
            if (neighborDistance >= currentBest || neighborDistance >= bestDistance)
            {
                return;
            }

            bestDistance = neighborDistance;
            bestDir = dirCode;
        }
    }
}
