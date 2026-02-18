using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Map
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(Project.Horde.HordeSeparationSystem))]
    public partial struct WallFieldBuildSystem : ISystem
    {
        private const int InfDistance = int.MaxValue;
        private const byte NoneDirection = 255;
        private const int DirCount = 32;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<MapWalkableCell>();
            state.RequireForUpdate<WallFieldDirtyTag>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton(out WallFieldSingleton singleton) && singleton.Blob.IsCreated)
            {
                singleton.Blob.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            Entity mapEntity = SystemAPI.GetSingletonEntity<MapRuntimeData>();
            if (!state.EntityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
            {
                return;
            }

            MapRuntimeData map = state.EntityManager.GetComponentData<MapRuntimeData>(mapEntity);
            DynamicBuffer<MapWalkableCell> walkable = state.EntityManager.GetBuffer<MapWalkableCell>(mapEntity);
            int width = map.Width;
            int height = map.Height;
            int tileCount = width * height;
            if (tileCount <= 0 || walkable.Length != tileCount)
            {
                state.EntityManager.RemoveComponent<WallFieldDirtyTag>(mapEntity);
                return;
            }

            NativeArray<int> dist = new NativeArray<int>(tileCount, Allocator.Temp);
            NativeArray<int> queue = new NativeArray<int>(tileCount, Allocator.Temp);
            NativeArray<byte> dir = new NativeArray<byte>(tileCount, Allocator.Temp);
            NativeArray<float2> dirLut = new NativeArray<float2>(DirCount, Allocator.Temp);

            try
            {
                BuildDirLut(dirLut);

                int head = 0;
                int tail = 0;
                for (int i = 0; i < tileCount; i++)
                {
                    bool blocked = !walkable[i].IsWalkable;
                    dist[i] = blocked ? 0 : InfDistance;
                    dir[i] = NoneDirection;
                    if (blocked)
                    {
                        queue[tail++] = i;
                    }
                }

                while (head < tail)
                {
                    int current = queue[head++];
                    int y = current / width;
                    int x = current - (y * width);
                    int nextDistance = dist[current] + 1;

                    Visit(x + 1, y, nextDistance, width, height, dist, queue, ref tail);
                    Visit(x - 1, y, nextDistance, width, height, dist, queue, ref tail);
                    Visit(x, y + 1, nextDistance, width, height, dist, queue, ref tail);
                    Visit(x, y - 1, nextDistance, width, height, dist, queue, ref tail);
                }

                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int index = row + x;
                        if (!walkable[index].IsWalkable || dist[index] == InfDistance)
                        {
                            dir[index] = NoneDirection;
                            continue;
                        }

                        int dL = x > 0 ? dist[index - 1] : dist[index];
                        int dR = x < width - 1 ? dist[index + 1] : dist[index];
                        int dD = y > 0 ? dist[index - width] : dist[index];
                        int dU = y < height - 1 ? dist[index + width] : dist[index];

                        float2 grad = new float2(dR - dL, dU - dD);
                        float lenSq = math.lengthsq(grad);
                        if (lenSq <= 0.000001f)
                        {
                            dir[index] = NoneDirection;
                            continue;
                        }

                        float2 n = grad * math.rsqrt(lenSq);
                        dir[index] = Quantize(n, dirLut);
                    }
                }

                BlobBuilder builder = new BlobBuilder(Allocator.Temp);
                ref WallFieldBlob root = ref builder.ConstructRoot<WallFieldBlob>();
                root.Width = width;
                root.Height = height;
                root.CellSize = map.TileSize;
                root.OriginWorld = map.Origin;

                BlobBuilderArray<ushort> distBlob = builder.Allocate(ref root.Dist, tileCount);
                BlobBuilderArray<byte> dirBlob = builder.Allocate(ref root.Dir, tileCount);
                BlobBuilderArray<float2> lutBlob = builder.Allocate(ref root.DirLut, DirCount);

                for (int i = 0; i < tileCount; i++)
                {
                    distBlob[i] = dist[i] == InfDistance ? ushort.MaxValue : (ushort)math.min(ushort.MaxValue, dist[i]);
                    dirBlob[i] = dir[i];
                }

                for (int i = 0; i < DirCount; i++)
                {
                    lutBlob[i] = dirLut[i];
                }

                BlobAssetReference<WallFieldBlob> blob = builder.CreateBlobAssetReference<WallFieldBlob>(Allocator.Persistent);
                builder.Dispose();

                Entity wallEntity = GetOrCreateWallEntity(ref state);
                WallFieldSingleton singleton = state.EntityManager.GetComponentData<WallFieldSingleton>(wallEntity);
                if (singleton.Blob.IsCreated)
                {
                    singleton.Blob.Dispose();
                }

                singleton.Blob = blob;
                state.EntityManager.SetComponentData(wallEntity, singleton);
                state.EntityManager.RemoveComponent<WallFieldDirtyTag>(mapEntity);

                UnityEngine.Debug.Log($"WallField built {width}x{height}");
            }
            finally
            {
                if (dist.IsCreated) dist.Dispose();
                if (queue.IsCreated) queue.Dispose();
                if (dir.IsCreated) dir.Dispose();
                if (dirLut.IsCreated) dirLut.Dispose();
            }
        }

        private static Entity GetOrCreateWallEntity(ref SystemState state)
        {
            EntityQuery query = state.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<WallFieldSingleton>());
            Entity entity;
            if (query.IsEmptyIgnoreFilter)
            {
                entity = state.EntityManager.CreateEntity(typeof(WallFieldSingleton));
                state.EntityManager.SetComponentData(entity, new WallFieldSingleton());
            }
            else
            {
                entity = query.GetSingletonEntity();
            }

            query.Dispose();
            return entity;
        }

        private static void Visit(int x, int y, int nextDistance, int width, int height, NativeArray<int> dist, NativeArray<int> queue, ref int tail)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            int idx = x + (y * width);
            if (nextDistance >= dist[idx])
            {
                return;
            }

            dist[idx] = nextDistance;
            queue[tail++] = idx;
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

        private static byte Quantize(float2 v, NativeArray<float2> lut)
        {
            int best = 0;
            float bestDot = -2f;
            for (int i = 0; i < lut.Length; i++)
            {
                float dot = math.dot(v, lut[i]);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = i;
                }
            }

            return (byte)best;
        }
    }
}
