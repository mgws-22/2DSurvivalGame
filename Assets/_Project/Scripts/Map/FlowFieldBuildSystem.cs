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
        private const int QuantizedDirCount = 32;
        private const float Diagonal = 0.70710677f;

        private static readonly int2[] NeighborOffsets8 =
        {
            new int2(0, 1),
            new int2(1, 1),
            new int2(1, 0),
            new int2(1, -1),
            new int2(0, -1),
            new int2(-1, -1),
            new int2(-1, 0),
            new int2(-1, 1)
        };

        private static readonly float2[] NeighborDirs8 =
        {
            new float2(0f, 1f),
            new float2(Diagonal, Diagonal),
            new float2(1f, 0f),
            new float2(Diagonal, -Diagonal),
            new float2(0f, -1f),
            new float2(-Diagonal, -Diagonal),
            new float2(-1f, 0f),
            new float2(-Diagonal, Diagonal)
        };

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

            MapRuntimeData map = state.EntityManager.GetComponentData<MapRuntimeData>(mapEntity);
            DynamicBuffer<MapWalkableCell> mapWalkable = state.EntityManager.GetBuffer<MapWalkableCell>(mapEntity);
            int mapTileCount = map.Width * map.Height;
            if (mapTileCount <= 0 || mapWalkable.Length != mapTileCount)
            {
                state.EntityManager.RemoveComponent<FlowFieldDirtyTag>(mapEntity);
                return;
            }

            int margin = math.max(0, map.SpawnMargin);
            int expWidth = map.Width + (margin * 2);
            int expHeight = map.Height + (margin * 2);
            int expTileCount = expWidth * expHeight;
            float2 expOrigin = map.Origin - (new float2(margin, margin) * map.TileSize);

            double buildStart = Time.realtimeSinceStartupAsDouble;

            NativeArray<byte> expandedWalkable = new NativeArray<byte>(expTileCount, Allocator.Temp);
            NativeArray<int> dist = new NativeArray<int>(expTileCount, Allocator.Temp);
            NativeArray<int> queue = new NativeArray<int>(expTileCount, Allocator.Temp);
            NativeArray<byte> dir = new NativeArray<byte>(expTileCount, Allocator.Temp);
            NativeArray<float2> dirLut = new NativeArray<float2>(QuantizedDirCount, Allocator.Temp);

            try
            {
                BuildDirLut(dirLut);

                for (int y = 0; y < expHeight; y++)
                {
                    int row = y * expWidth;
                    int oy = y - margin;
                    for (int x = 0; x < expWidth; x++)
                    {
                        int ox = x - margin;
                        bool walkable = true;
                        if (ox >= 0 && oy >= 0 && ox < map.Width && oy < map.Height)
                        {
                            walkable = mapWalkable[ox + (oy * map.Width)].IsWalkable;
                        }

                        int index = row + x;
                        expandedWalkable[index] = walkable ? (byte)1 : (byte)0;
                        dist[index] = InfDistance;
                        dir[index] = NoneDirection;
                    }
                }

                int2 mapCenter = map.WorldToGrid(map.CenterWorld);
                int2 expCenter = mapCenter + new int2(margin, margin);
                int radius = math.max(0, map.CenterOpenRadius);
                int radiusSq = radius * radius;
                int head = 0;
                int tail = 0;

                int minY = math.max(0, expCenter.y - radius);
                int maxY = math.min(expHeight - 1, expCenter.y + radius);
                for (int y = minY; y <= maxY; y++)
                {
                    int dy = y - expCenter.y;
                    int row = y * expWidth;
                    int minX = math.max(0, expCenter.x - radius);
                    int maxX = math.min(expWidth - 1, expCenter.x + radius);

                    for (int x = minX; x <= maxX; x++)
                    {
                        int dx = x - expCenter.x;
                        if ((dx * dx) + (dy * dy) > radiusSq)
                        {
                            continue;
                        }

                        int index = row + x;
                        if (expandedWalkable[index] == 0 || dist[index] == 0)
                        {
                            continue;
                        }

                        dist[index] = 0;
                        queue[tail++] = index;
                    }
                }

                if (tail == 0)
                {
                    int cx = math.clamp(expCenter.x, 0, expWidth - 1);
                    int cy = math.clamp(expCenter.y, 0, expHeight - 1);
                    int fallback = cx + (cy * expWidth);
                    if (expandedWalkable[fallback] != 0)
                    {
                        dist[fallback] = 0;
                        queue[tail++] = fallback;
                    }
                    else
                    {
                        for (int i = 0; i < expTileCount; i++)
                        {
                            if (expandedWalkable[i] == 0)
                            {
                                continue;
                            }

                            dist[i] = 0;
                            queue[tail++] = i;
                            break;
                        }
                    }
                }

                while (head < tail)
                {
                    int currentIndex = queue[head++];
                    int y = currentIndex / expWidth;
                    int x = currentIndex - (y * expWidth);
                    int nextDistance = dist[currentIndex] + 1;

                    VisitNeighbor(x, y + 1, nextDistance, expWidth, expHeight, expandedWalkable, dist, queue, ref tail);
                    VisitNeighbor(x + 1, y, nextDistance, expWidth, expHeight, expandedWalkable, dist, queue, ref tail);
                    VisitNeighbor(x, y - 1, nextDistance, expWidth, expHeight, expandedWalkable, dist, queue, ref tail);
                    VisitNeighbor(x - 1, y, nextDistance, expWidth, expHeight, expandedWalkable, dist, queue, ref tail);
                }

                int reachableCount = 0;
                for (int y = 0; y < expHeight; y++)
                {
                    int row = y * expWidth;
                    for (int x = 0; x < expWidth; x++)
                    {
                        int index = row + x;
                        if (expandedWalkable[index] == 0 || dist[index] == InfDistance)
                        {
                            dir[index] = NoneDirection;
                            continue;
                        }

                        reachableCount++;
                        int currentDistance = dist[index];

                        if (currentDistance == 0)
                        {
                            float2 toCenter = new float2(expCenter.x - x, expCenter.y - y);
                            if (math.lengthsq(toCenter) <= 0.000001f)
                            {
                                dir[index] = 0;
                            }
                            else
                            {
                                float2 v0 = toCenter * math.rsqrt(math.lengthsq(toCenter));
                                dir[index] = QuantizeDirection(v0, dirLut);
                            }

                            continue;
                        }

                        float2 acc = float2.zero;
                        for (int n = 0; n < NeighborOffsets8.Length; n++)
                        {
                            int2 offset = NeighborOffsets8[n];
                            int nx = x + offset.x;
                            int ny = y + offset.y;
                            if (nx < 0 || ny < 0 || nx >= expWidth || ny >= expHeight)
                            {
                                continue;
                            }

                            if (offset.x != 0 && offset.y != 0 &&
                                (!IsWalkableExpanded(x + offset.x, y, expWidth, expHeight, expandedWalkable) ||
                                 !IsWalkableExpanded(x, y + offset.y, expWidth, expHeight, expandedWalkable)))
                            {
                                continue;
                            }

                            int nIndex = nx + (ny * expWidth);
                            if (expandedWalkable[nIndex] == 0)
                            {
                                continue;
                            }

                            int nDistance = dist[nIndex];
                            if (nDistance == InfDistance || nDistance >= currentDistance)
                            {
                                continue;
                            }

                            int delta = currentDistance - nDistance;
                            acc += NeighborDirs8[n] * delta;
                        }

                        if (math.lengthsq(acc) <= 0.000001f)
                        {
                            float2 fallbackDir = float2.zero;
                            int bestDistance = currentDistance;

                            for (int n = 0; n < NeighborOffsets8.Length; n++)
                            {
                                int2 offset = NeighborOffsets8[n];
                                int nx = x + offset.x;
                                int ny = y + offset.y;
                                if (nx < 0 || ny < 0 || nx >= expWidth || ny >= expHeight)
                                {
                                    continue;
                                }

                                if (offset.x != 0 && offset.y != 0 &&
                                    (!IsWalkableExpanded(x + offset.x, y, expWidth, expHeight, expandedWalkable) ||
                                     !IsWalkableExpanded(x, y + offset.y, expWidth, expHeight, expandedWalkable)))
                                {
                                    continue;
                                }

                                int nIndex = nx + (ny * expWidth);
                                if (expandedWalkable[nIndex] == 0)
                                {
                                    continue;
                                }

                                int nDistance = dist[nIndex];
                                if (nDistance < bestDistance)
                                {
                                    bestDistance = nDistance;
                                    fallbackDir = NeighborDirs8[n];
                                }
                            }

                            if (bestDistance < currentDistance)
                            {
                                dir[index] = QuantizeDirection(fallbackDir, dirLut);
                                continue;
                            }

                            float2 toCenter = new float2(expCenter.x - x, expCenter.y - y);
                            if (math.lengthsq(toCenter) <= 0.000001f)
                            {
                                dir[index] = 0;
                            }
                            else
                            {
                                float2 vc = toCenter * math.rsqrt(math.lengthsq(toCenter));
                                dir[index] = QuantizeDirection(vc, dirLut);
                            }

                            continue;
                        }

                        float2 v = acc * math.rsqrt(math.lengthsq(acc));
                        dir[index] = QuantizeDirection(v, dirLut);
                    }
                }

                BlobBuilder builder = new BlobBuilder(Allocator.Temp);
                ref FlowFieldBlob root = ref builder.ConstructRoot<FlowFieldBlob>();
                root.Width = expWidth;
                root.Height = expHeight;
                root.DirCount = QuantizedDirCount;
                root.CellSize = map.TileSize;
                root.OriginWorld = expOrigin;

                BlobBuilderArray<byte> dirBlob = builder.Allocate(ref root.Dir, expTileCount);
                BlobBuilderArray<ushort> distBlob = builder.Allocate(ref root.Dist, expTileCount);
                BlobBuilderArray<float2> lutBlob = builder.Allocate(ref root.DirLut, QuantizedDirCount);

                for (int i = 0; i < expTileCount; i++)
                {
                    dirBlob[i] = dir[i];
                    distBlob[i] = dist[i] == InfDistance ? ushort.MaxValue : (ushort)math.min(ushort.MaxValue, dist[i]);
                }

                for (int i = 0; i < QuantizedDirCount; i++)
                {
                    lutBlob[i] = dirLut[i];
                }

                BlobAssetReference<FlowFieldBlob> blob = builder.CreateBlobAssetReference<FlowFieldBlob>(Allocator.Persistent);
                builder.Dispose();

                Entity flowEntity = GetOrCreateFlowEntity(ref state);
                FlowFieldSingleton flowSingleton = state.EntityManager.GetComponentData<FlowFieldSingleton>(flowEntity);
                if (flowSingleton.Blob.IsCreated)
                {
                    flowSingleton.Blob.Dispose();
                }

                flowSingleton.Blob = blob;
                state.EntityManager.SetComponentData(flowEntity, flowSingleton);
                state.EntityManager.RemoveComponent<FlowFieldDirtyTag>(mapEntity);

                double buildMs = (Time.realtimeSinceStartupAsDouble - buildStart) * 1000.0;
                UnityEngine.Debug.Log($"FlowField built {expWidth}x{expHeight} in {buildMs:F2} ms. Reachable={reachableCount}");
            }
            finally
            {
                if (expandedWalkable.IsCreated)
                {
                    expandedWalkable.Dispose();
                }

                if (dist.IsCreated)
                {
                    dist.Dispose();
                }

                if (queue.IsCreated)
                {
                    queue.Dispose();
                }

                if (dir.IsCreated)
                {
                    dir.Dispose();
                }

                if (dirLut.IsCreated)
                {
                    dirLut.Dispose();
                }
            }
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
            NativeArray<byte> walkable,
            NativeArray<int> dist,
            NativeArray<int> queue,
            ref int tail)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            int index = x + (y * width);
            if (walkable[index] == 0 || nextDistance >= dist[index])
            {
                return;
            }

            dist[index] = nextDistance;
            queue[tail++] = index;
        }

        private static bool IsWalkableExpanded(int x, int y, int width, int height, NativeArray<byte> walkable)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return false;
            }

            return walkable[x + (y * width)] != 0;
        }

        private static void BuildDirLut(NativeArray<float2> lut)
        {
            float step = (math.PI * 2f) / lut.Length;
            for (int i = 0; i < lut.Length; i++)
            {
                float angle = i * step;
                math.sincos(angle, out float s, out float c);
                lut[i] = new float2(c, s);
            }
        }

        private static byte QuantizeDirection(float2 v, NativeArray<float2> lut)
        {
            int bestIndex = 0;
            float bestDot = -2f;
            for (int i = 0; i < lut.Length; i++)
            {
                float dot = math.dot(v, lut[i]);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestIndex = i;
                }
            }

            return (byte)bestIndex;
        }
    }
}
