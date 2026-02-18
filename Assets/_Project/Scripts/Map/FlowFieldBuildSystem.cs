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
            new int2(0, 1),   // N
            new int2(1, 1),   // NE
            new int2(1, 0),   // E
            new int2(1, -1),  // SE
            new int2(0, -1),  // S
            new int2(-1, -1), // SW
            new int2(-1, 0),  // W
            new int2(-1, 1)   // NW
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
            DynamicBuffer<MapWalkableCell> walkableBuffer = state.EntityManager.GetBuffer<MapWalkableCell>(mapEntity);
            int tileCount = map.Width * map.Height;
            if (tileCount <= 0 || walkableBuffer.Length != tileCount)
            {
                state.EntityManager.RemoveComponent<FlowFieldDirtyTag>(mapEntity);
                return;
            }

            int gateCount = 0;
            if (state.EntityManager.HasBuffer<GatePoint>(mapEntity))
            {
                DynamicBuffer<GatePoint> gates = state.EntityManager.GetBuffer<GatePoint>(mapEntity);
                gateCount = gates.Length;
            }

            double buildStart = Time.realtimeSinceStartupAsDouble;

            NativeArray<int> dist = new NativeArray<int>(tileCount, Allocator.Temp);
            NativeArray<int> queue = new NativeArray<int>(tileCount, Allocator.Temp);
            NativeArray<byte> dir = new NativeArray<byte>(tileCount, Allocator.Temp);
            NativeArray<float2> dirLut = new NativeArray<float2>(QuantizedDirCount, Allocator.Temp);
            try
            {
                BuildDirLut(dirLut);

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
                    int currentIndex = queue[head++];
                    int y = currentIndex / map.Width;
                    int x = currentIndex - (y * map.Width);
                    int nextDistance = dist[currentIndex] + 1;

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
                        int currentDistance = dist[index];
                        float2 acc = float2.zero;

                        for (int n = 0; n < NeighborOffsets8.Length; n++)
                        {
                            int2 offset = NeighborOffsets8[n];
                            int nx = x + offset.x;
                            int ny = y + offset.y;
                            if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height)
                            {
                                continue;
                            }

                            if (offset.x != 0 && offset.y != 0 &&
                                (!IsWalkable(x + offset.x, y, map.Width, map.Height, walkableBuffer) ||
                                 !IsWalkable(x, y + offset.y, map.Width, map.Height, walkableBuffer)))
                            {
                                continue;
                            }

                            int nIndex = nx + (ny * map.Width);
                            if (!walkableBuffer[nIndex].IsWalkable)
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
                            int bestDelta = 0;

                            for (int n = 0; n < NeighborOffsets8.Length; n++)
                            {
                                int2 offset = NeighborOffsets8[n];
                                int nx = x + offset.x;
                                int ny = y + offset.y;
                                if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height)
                                {
                                    continue;
                                }

                                if (offset.x != 0 && offset.y != 0 &&
                                    (!IsWalkable(x + offset.x, y, map.Width, map.Height, walkableBuffer) ||
                                     !IsWalkable(x, y + offset.y, map.Width, map.Height, walkableBuffer)))
                                {
                                    continue;
                                }

                                int nIndex = nx + (ny * map.Width);
                                if (!walkableBuffer[nIndex].IsWalkable)
                                {
                                    continue;
                                }

                                int nDistance = dist[nIndex];
                                if (nDistance == InfDistance || nDistance >= currentDistance)
                                {
                                    continue;
                                }

                                int delta = currentDistance - nDistance;
                                if (delta > bestDelta)
                                {
                                    bestDelta = delta;
                                    fallbackDir = NeighborDirs8[n];
                                }
                            }

                            if (bestDelta <= 0)
                            {
                                dir[index] = NoneDirection;
                                continue;
                            }

                            dir[index] = QuantizeDirection(fallbackDir, dirLut);
                            continue;
                        }

                        float2 v = acc * math.rsqrt(math.lengthsq(acc));
                        dir[index] = QuantizeDirection(v, dirLut);
                    }
                }

                BlobBuilder builder = new BlobBuilder(Allocator.Temp);
                ref FlowFieldBlob root = ref builder.ConstructRoot<FlowFieldBlob>();
                root.Width = map.Width;
                root.Height = map.Height;
                root.DirCount = QuantizedDirCount;
                root.CellSize = map.TileSize;
                root.OriginWorld = map.Origin;

                BlobBuilderArray<byte> dirBlob = builder.Allocate(ref root.Dir, tileCount);
                BlobBuilderArray<ushort> distBlob = builder.Allocate(ref root.Dist, tileCount);
                BlobBuilderArray<float2> lutBlob = builder.Allocate(ref root.DirLut, QuantizedDirCount);
                for (int i = 0; i < tileCount; i++)
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
                UnityEngine.Debug.Log($"FlowField built {map.Width}x{map.Height} in {buildMs:F2} ms. Reachable={reachableCount} Gates={gateCount}");
            }
            finally
            {
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

        private static bool IsWalkable(int x, int y, int width, int height, DynamicBuffer<MapWalkableCell> walkable)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return false;
            }

            return walkable[x + (y * width)].IsWalkable;
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
