using System.Diagnostics;
using Project.Buildings;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
        private const double DebugLogIntervalSeconds = 1.0;

        private EntityQuery _mapQuery;
        private EntityQuery _wallFieldQuery;
        private EntityQuery _dynamicObstacleRegistryQuery;

        private NativeArray<byte> _baseBlockedExpanded;
        private NativeArray<byte> _obstacleBlockedExpanded;
        private NativeArray<int> _distance;
        private NativeArray<int> _queue;
        private NativeArray<byte> _dir;
        private NativeArray<float2> _dirLut;
        private int _bufferTileCount;

        private JobHandle _rebuildHandle;
        private bool _rebuildInFlight;

        private MapRuntimeData _scheduledMap;
        private int _scheduledWalkableLength;
        private int _scheduledExpandedWidth;
        private int _scheduledExpandedHeight;
        private int _scheduledTileCount;
        private int _scheduledRectCount;
        private float2 _scheduledExpandedOriginWorld;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private double _nextDebugLogTime;
#endif

        public void OnCreate(ref SystemState state)
        {
            _mapQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<MapRuntimeData>(),
                ComponentType.ReadOnly<MapWalkableCell>());
            _wallFieldQuery = state.GetEntityQuery(ComponentType.ReadWrite<WallFieldSingleton>());
            _dynamicObstacleRegistryQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<DynamicObstacleRegistryTag>(),
                ComponentType.ReadOnly<DynamicObstacleRect>());

            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<MapWalkableCell>();
            state.RequireForUpdate<WallFieldDirtyTag>();

            _bufferTileCount = 0;
            _rebuildHandle = default;
            _rebuildInFlight = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _nextDebugLogTime = 0.0;
#endif
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_rebuildInFlight)
            {
                _rebuildHandle.Complete();
                _rebuildInFlight = false;
            }

            if (!_wallFieldQuery.IsEmptyIgnoreFilter)
            {
                Entity singletonEntity = _wallFieldQuery.GetSingletonEntity();
                WallFieldSingleton singleton = state.EntityManager.GetComponentData<WallFieldSingleton>(singletonEntity);
                if (singleton.Blob.IsCreated)
                {
                    singleton.Blob.Dispose();
                }
            }

            DisposePersistentBuffers();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_mapQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity mapEntity = _mapQuery.GetSingletonEntity();
            EnsureStatsComponent(ref state, mapEntity);

            if (_rebuildInFlight)
            {
                if (!_rebuildHandle.IsCompleted)
                {
                    return;
                }

                CompleteAndSwapRebuild(ref state, mapEntity);
            }

            if (!state.EntityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
            {
                return;
            }

            TryScheduleRebuild(ref state, mapEntity);
        }

        private void TryScheduleRebuild(ref SystemState state, Entity mapEntity)
        {
            MapRuntimeData map = state.EntityManager.GetComponentData<MapRuntimeData>(mapEntity);
            DynamicBuffer<MapWalkableCell> walkable = state.EntityManager.GetBuffer<MapWalkableCell>(mapEntity);

            int mapWidth = map.Width;
            int mapHeight = map.Height;
            int mapTileCount = mapWidth * mapHeight;
            if (mapTileCount <= 0 || walkable.Length != mapTileCount)
            {
                if (state.EntityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
                {
                    state.EntityManager.RemoveComponent<WallFieldDirtyTag>(mapEntity);
                }

                return;
            }

            int margin = math.max(0, map.SpawnMargin);
            int expandedWidth = mapWidth + (margin * 2);
            int expandedHeight = mapHeight + (margin * 2);
            int expandedTileCount = expandedWidth * expandedHeight;
            if (expandedTileCount <= 0)
            {
                if (state.EntityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
                {
                    state.EntityManager.RemoveComponent<WallFieldDirtyTag>(mapEntity);
                }

                return;
            }

            EnsureBufferCapacity(expandedTileCount);
            FillBaseBlockedExpanded(map, walkable, margin, expandedWidth, expandedHeight, expandedTileCount);

            int rectCount = 0;
            if (!_dynamicObstacleRegistryQuery.IsEmptyIgnoreFilter)
            {
                Entity registryEntity = _dynamicObstacleRegistryQuery.GetSingletonEntity();
                DynamicBuffer<DynamicObstacleRect> obstacleRects = state.EntityManager.GetBuffer<DynamicObstacleRect>(registryEntity);
                rectCount = obstacleRects.Length;
                FillObstacleMaskExpanded(obstacleRects, margin, expandedWidth, expandedHeight, expandedTileCount);
            }
            else
            {
                ClearByteBuffer(_obstacleBlockedExpanded, expandedTileCount);
            }

            _scheduledMap = map;
            _scheduledWalkableLength = walkable.Length;
            _scheduledExpandedWidth = expandedWidth;
            _scheduledExpandedHeight = expandedHeight;
            _scheduledTileCount = expandedTileCount;
            _scheduledRectCount = rectCount;
            _scheduledExpandedOriginWorld = map.Origin - (new float2(margin, margin) * map.TileSize);

            BuildWallFieldJob job = new BuildWallFieldJob
            {
                Width = expandedWidth,
                Height = expandedHeight,
                TileCount = expandedTileCount,
                BaseBlockedExpanded = _baseBlockedExpanded,
                ObstacleBlockedExpanded = _obstacleBlockedExpanded,
                Distance = _distance,
                Queue = _queue,
                Dir = _dir,
                DirLut = _dirLut
            };

            _rebuildHandle = job.Schedule();
            _rebuildInFlight = true;
        }

        private void CompleteAndSwapRebuild(ref SystemState state, Entity mapEntity)
        {
            _rebuildHandle.Complete();
            _rebuildInFlight = false;

            long t0 = Stopwatch.GetTimestamp();

            BlobBuilder builder = new BlobBuilder(Allocator.Temp);
            ref WallFieldBlob root = ref builder.ConstructRoot<WallFieldBlob>();
            root.Width = _scheduledExpandedWidth;
            root.Height = _scheduledExpandedHeight;
            root.CellSize = _scheduledMap.TileSize;
            root.OriginWorld = _scheduledExpandedOriginWorld;

            BlobBuilderArray<ushort> distBlob = builder.Allocate(ref root.Dist, _scheduledTileCount);
            BlobBuilderArray<byte> dirBlob = builder.Allocate(ref root.Dir, _scheduledTileCount);
            BlobBuilderArray<float2> lutBlob = builder.Allocate(ref root.DirLut, DirCount);

            for (int i = 0; i < _scheduledTileCount; i++)
            {
                int d = _distance[i];
                distBlob[i] = d == InfDistance ? ushort.MaxValue : (ushort)math.min(ushort.MaxValue, d);
                dirBlob[i] = _dir[i];
            }

            for (int i = 0; i < DirCount; i++)
            {
                lutBlob[i] = _dirLut[i];
            }

            BlobAssetReference<WallFieldBlob> blob = builder.CreateBlobAssetReference<WallFieldBlob>(Allocator.Persistent);
            builder.Dispose();

            Entity wallEntity = GetOrCreateWallEntity(ref state);
            WallFieldSingleton singleton = state.EntityManager.GetComponentData<WallFieldSingleton>(wallEntity);
            BlobAssetReference<WallFieldBlob> oldBlob = singleton.Blob;
            singleton.Blob = blob;
            state.EntityManager.SetComponentData(wallEntity, singleton);
            if (oldBlob.IsCreated)
            {
                oldBlob.Dispose();
            }

            bool snapshotStillCurrent = SnapshotMatchesCurrentState(ref state, mapEntity);
            if (snapshotStillCurrent && state.EntityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
            {
                state.EntityManager.RemoveComponent<WallFieldDirtyTag>(mapEntity);
            }

            float elapsedMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            UpdateStats(ref state, mapEntity, _scheduledRectCount, elapsedMs);
        }

        private bool SnapshotMatchesCurrentState(ref SystemState state, Entity mapEntity)
        {
            if (!state.EntityManager.Exists(mapEntity))
            {
                return false;
            }

            if (!state.EntityManager.HasComponent<MapRuntimeData>(mapEntity) ||
                !state.EntityManager.HasBuffer<MapWalkableCell>(mapEntity))
            {
                return false;
            }

            MapRuntimeData currentMap = state.EntityManager.GetComponentData<MapRuntimeData>(mapEntity);
            DynamicBuffer<MapWalkableCell> currentWalkable = state.EntityManager.GetBuffer<MapWalkableCell>(mapEntity);

            if (currentMap.Width != _scheduledMap.Width ||
                currentMap.Height != _scheduledMap.Height ||
                currentMap.SpawnMargin != _scheduledMap.SpawnMargin ||
                currentMap.TileSize != _scheduledMap.TileSize ||
                !math.all(currentMap.Origin == _scheduledMap.Origin) ||
                currentWalkable.Length != _scheduledWalkableLength)
            {
                return false;
            }

            int currentRectCount = 0;
            if (!_dynamicObstacleRegistryQuery.IsEmptyIgnoreFilter)
            {
                Entity registryEntity = _dynamicObstacleRegistryQuery.GetSingletonEntity();
                if (state.EntityManager.Exists(registryEntity))
                {
                    currentRectCount = state.EntityManager.GetBuffer<DynamicObstacleRect>(registryEntity).Length;
                }
            }

            return currentRectCount == _scheduledRectCount;
        }

        private void EnsureStatsComponent(ref SystemState state, Entity mapEntity)
        {
            if (!state.EntityManager.HasComponent<WallFieldStats>(mapEntity))
            {
                state.EntityManager.AddComponentData(mapEntity, new WallFieldStats());
            }
        }

        private void UpdateStats(ref SystemState state, Entity mapEntity, int rectCount, float elapsedMs)
        {
            if (!state.EntityManager.Exists(mapEntity))
            {
                return;
            }

            EnsureStatsComponent(ref state, mapEntity);

            WallFieldStats stats = state.EntityManager.GetComponentData<WallFieldStats>(mapEntity);
            stats.RebuildCount++;
            stats.RectCount = rectCount;
            stats.LastRebuildMs = elapsedMs;
            state.EntityManager.SetComponentData(mapEntity, stats);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            double now = SystemAPI.Time.ElapsedTime;
            if (now >= _nextDebugLogTime)
            {
                _nextDebugLogTime = now + DebugLogIntervalSeconds;
                UnityEngine.Debug.Log(
                    "[WallFieldBuildSystem] RebuildCount=" + stats.RebuildCount +
                    " RectCount=" + stats.RectCount +
                    " LastRebuildMs=" + stats.LastRebuildMs.ToString("F2"));
            }
#endif
        }

        private void EnsureBufferCapacity(int tileCount)
        {
            if (_bufferTileCount == tileCount &&
                _baseBlockedExpanded.IsCreated &&
                _obstacleBlockedExpanded.IsCreated &&
                _distance.IsCreated &&
                _queue.IsCreated &&
                _dir.IsCreated &&
                _dirLut.IsCreated)
            {
                return;
            }

            if (_rebuildInFlight)
            {
                _rebuildHandle.Complete();
                _rebuildInFlight = false;
            }

            DisposePersistentBuffers();

            _baseBlockedExpanded = new NativeArray<byte>(tileCount, Allocator.Persistent);
            _obstacleBlockedExpanded = new NativeArray<byte>(tileCount, Allocator.Persistent);
            _distance = new NativeArray<int>(tileCount, Allocator.Persistent);
            _queue = new NativeArray<int>(tileCount, Allocator.Persistent);
            _dir = new NativeArray<byte>(tileCount, Allocator.Persistent);
            _dirLut = new NativeArray<float2>(DirCount, Allocator.Persistent);
            _bufferTileCount = tileCount;

            BuildDirLut(_dirLut);
        }

        private void DisposePersistentBuffers()
        {
            if (_baseBlockedExpanded.IsCreated) _baseBlockedExpanded.Dispose();
            if (_obstacleBlockedExpanded.IsCreated) _obstacleBlockedExpanded.Dispose();
            if (_distance.IsCreated) _distance.Dispose();
            if (_queue.IsCreated) _queue.Dispose();
            if (_dir.IsCreated) _dir.Dispose();
            if (_dirLut.IsCreated) _dirLut.Dispose();
            _bufferTileCount = 0;
        }

        private void FillBaseBlockedExpanded(
            MapRuntimeData map,
            DynamicBuffer<MapWalkableCell> walkable,
            int margin,
            int expandedWidth,
            int expandedHeight,
            int expandedTileCount)
        {
            if (expandedTileCount <= 0)
            {
                return;
            }

            int mapWidth = map.Width;
            int mapHeight = map.Height;

            for (int y = 0; y < expandedHeight; y++)
            {
                int row = y * expandedWidth;
                int mapY = y - margin;
                for (int x = 0; x < expandedWidth; x++)
                {
                    int mapX = x - margin;
                    bool blocked = false;
                    if (mapX >= 0 && mapY >= 0 && mapX < mapWidth && mapY < mapHeight)
                    {
                        int mapIndex = mapX + (mapY * mapWidth);
                        blocked = !walkable[mapIndex].IsWalkable;
                    }

                    _baseBlockedExpanded[row + x] = blocked ? (byte)1 : (byte)0;
                }
            }
        }

        private void FillObstacleMaskExpanded(
            DynamicBuffer<DynamicObstacleRect> obstacleRects,
            int margin,
            int expandedWidth,
            int expandedHeight,
            int expandedTileCount)
        {
            ClearByteBuffer(_obstacleBlockedExpanded, expandedTileCount);

            int2 max = new int2(expandedWidth, expandedHeight);
            for (int i = 0; i < obstacleRects.Length; i++)
            {
                DynamicObstacleRect rect = obstacleRects[i];
                int2 minExpanded = rect.MinCell + new int2(margin, margin);
                int2 maxExpanded = rect.MaxCellExclusive + new int2(margin, margin);

                minExpanded = math.clamp(minExpanded, int2.zero, max);
                maxExpanded = math.clamp(maxExpanded, int2.zero, max);
                if (minExpanded.x >= maxExpanded.x || minExpanded.y >= maxExpanded.y)
                {
                    continue;
                }

                for (int y = minExpanded.y; y < maxExpanded.y; y++)
                {
                    int row = y * expandedWidth;
                    for (int x = minExpanded.x; x < maxExpanded.x; x++)
                    {
                        _obstacleBlockedExpanded[row + x] = 1;
                    }
                }
            }
        }

        private static void ClearByteBuffer(NativeArray<byte> buffer, int length)
        {
            int max = math.min(length, buffer.Length);
            for (int i = 0; i < max; i++)
            {
                buffer[i] = 0;
            }
        }

        private Entity GetOrCreateWallEntity(ref SystemState state)
        {
            Entity entity;
            if (_wallFieldQuery.IsEmptyIgnoreFilter)
            {
                entity = state.EntityManager.CreateEntity(typeof(WallFieldSingleton));
                state.EntityManager.SetComponentData(entity, new WallFieldSingleton());
            }
            else
            {
                entity = _wallFieldQuery.GetSingletonEntity();
            }

            return entity;
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

        [BurstCompile]
        private struct BuildWallFieldJob : IJob
        {
            private const int JobInfDistance = int.MaxValue;
            private const byte JobNoneDirection = 255;

            public int Width;
            public int Height;
            public int TileCount;

            [ReadOnly] public NativeArray<byte> BaseBlockedExpanded;
            [ReadOnly] public NativeArray<byte> ObstacleBlockedExpanded;
            public NativeArray<int> Distance;
            public NativeArray<int> Queue;
            public NativeArray<byte> Dir;
            [ReadOnly] public NativeArray<float2> DirLut;

            public void Execute()
            {
                int head = 0;
                int tail = 0;

                for (int i = 0; i < TileCount; i++)
                {
                    bool blocked = BaseBlockedExpanded[i] != 0 || ObstacleBlockedExpanded[i] != 0;
                    Distance[i] = blocked ? 0 : JobInfDistance;
                    Dir[i] = JobNoneDirection;
                    if (blocked)
                    {
                        Queue[tail++] = i;
                    }
                }

                while (head < tail)
                {
                    int current = Queue[head++];
                    int y = current / Width;
                    int x = current - (y * Width);
                    int nextDistance = Distance[current] + 1;

                    Visit(x + 1, y, nextDistance, Width, Height, Distance, Queue, ref tail);
                    Visit(x - 1, y, nextDistance, Width, Height, Distance, Queue, ref tail);
                    Visit(x, y + 1, nextDistance, Width, Height, Distance, Queue, ref tail);
                    Visit(x, y - 1, nextDistance, Width, Height, Distance, Queue, ref tail);
                }

                for (int y = 0; y < Height; y++)
                {
                    int row = y * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        int index = row + x;
                        bool blocked = BaseBlockedExpanded[index] != 0 || ObstacleBlockedExpanded[index] != 0;
                        if (blocked || Distance[index] == JobInfDistance)
                        {
                            Dir[index] = JobNoneDirection;
                            continue;
                        }

                        int dL = x > 0 ? Distance[index - 1] : Distance[index];
                        int dR = x < Width - 1 ? Distance[index + 1] : Distance[index];
                        int dD = y > 0 ? Distance[index - Width] : Distance[index];
                        int dU = y < Height - 1 ? Distance[index + Width] : Distance[index];

                        float2 grad = new float2(dR - dL, dU - dD);
                        float lenSq = math.lengthsq(grad);
                        if (lenSq <= 0.000001f)
                        {
                            Dir[index] = JobNoneDirection;
                            continue;
                        }

                        float2 n = grad * math.rsqrt(lenSq);
                        Dir[index] = Quantize(n, DirLut);
                    }
                }
            }

            private static void Visit(
                int x,
                int y,
                int nextDistance,
                int width,
                int height,
                NativeArray<int> dist,
                NativeArray<int> queue,
                ref int tail)
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
}
