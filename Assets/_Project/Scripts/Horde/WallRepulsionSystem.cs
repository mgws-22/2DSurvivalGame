using Project.Map;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(HordePressureFieldSystem))]
    [UpdateAfter(typeof(HordeSeparationSystem))]
    [UpdateAfter(typeof(HordeHardSeparationSystem))]
    public partial struct WallRepulsionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ZombieTag>();
            state.RequireForUpdate<ZombieMoveSpeed>();
            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<MapWalkableCell>();
            state.RequireForUpdate<WallFieldSingleton>();

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<WallRepulsionConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity e = state.EntityManager.CreateEntity(typeof(WallRepulsionConfig));
                state.EntityManager.SetComponentData(e, new WallRepulsionConfig
                {
                    UnitRadiusWorld = 0.2f,
                    WallPushStrength = 10.4f,
                    MaxWallPushPerFrame = 0.35f,
                    ProjectionSearchRadiusCells = 1
                });
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            WallFieldSingleton wallSingleton = SystemAPI.GetSingleton<WallFieldSingleton>();
            if (!wallSingleton.Blob.IsCreated)
            {
                return;
            }

            MapRuntimeData map = SystemAPI.GetSingleton<MapRuntimeData>();
            NativeArray<MapWalkableCell> walkable = SystemAPI.GetSingletonBuffer<MapWalkableCell>(true).AsNativeArray();
            WallRepulsionConfig config = SystemAPI.GetSingleton<WallRepulsionConfig>();

            WallRepulsionJob job = new WallRepulsionJob
            {
                Map = map,
                Walkable = walkable,
                Wall = wallSingleton.Blob,
                UnitRadius = math.max(0.001f, config.UnitRadiusWorld),
                WallPushStrength = math.max(0f, config.WallPushStrength),
                MaxPush = math.max(0f, config.MaxWallPushPerFrame) * deltaTime,
                ProjectionRadius = math.clamp(config.ProjectionSearchRadiusCells, 1, 2),
                DeltaTime = deltaTime
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct WallRepulsionJob : IJobEntity
        {
            private const float ProjectionInsetFactor = 0.001f;

            public MapRuntimeData Map;
            [ReadOnly] public NativeArray<MapWalkableCell> Walkable;
            [ReadOnly] public BlobAssetReference<WallFieldBlob> Wall;
            public float UnitRadius;
            public float WallPushStrength;
            public float MaxPush;
            public int ProjectionRadius;
            public float DeltaTime;

            private void Execute(ref LocalTransform transform, in ZombieTag tag, in ZombieMoveSpeed moveSpeed)
            {
                float2 pos = transform.Position.xy;
                int2 cell = Map.WorldToGrid(pos);
                ref WallFieldBlob wall = ref Wall.Value;

                if (Map.IsInMap(cell))
                {
                    int index = Map.ToIndex(cell);
                    if (index < 0 || index >= Walkable.Length)
                    {
                        return;
                    }

                    if (!Walkable[index].IsWalkable)
                    {
                        // Hard wall-safety correction: do not speed-cap projection out of blocked tiles.
                        pos = ProjectToNearestWalkable(cell, pos);
                        transform.Position = new float3(pos.x, pos.y, transform.Position.z);
                        return;
                    }
                }

                int2 wallCell = WorldToWallGrid(pos, ref wall);
                if (IsInWallBounds(wallCell, ref wall))
                {
                    int wallIndex = wallCell.x + (wallCell.y * wall.Width);
                    if (wallIndex >= 0 && wallIndex < wall.Dist.Length && wallIndex < wall.Dir.Length)
                    {
                        float d = wall.Dist[wallIndex] == ushort.MaxValue ? float.MaxValue : wall.Dist[wallIndex] * wall.CellSize;
                        if (d < UnitRadius)
                        {
                            byte dir = wall.Dir[wallIndex];
                            if (dir < wall.DirLut.Length)
                            {
                                float2 n = wall.DirLut[dir];
                                float push = (UnitRadius - d) * WallPushStrength;
                                float maxStepBySpeed = math.max(0f, moveSpeed.Value) * DeltaTime;
                                float effectiveMaxPush = math.min(MaxPush, maxStepBySpeed);
                                push = math.min(push, effectiveMaxPush);
                                pos += n * push;
                            }
                        }
                    }
                }

                int2 nextCell = Map.WorldToGrid(pos);
                if (Map.IsInMap(nextCell))
                {
                    int nextIndex = Map.ToIndex(nextCell);
                    if (nextIndex >= 0 && nextIndex < Walkable.Length && !Walkable[nextIndex].IsWalkable)
                    {
                        // Hard wall-safety correction remains uncapped.
                        pos = ProjectToNearestWalkable(nextCell, pos);
                    }
                }

                transform.Position = new float3(pos.x, pos.y, transform.Position.z);
            }

            private static int2 WorldToWallGrid(float2 world, ref WallFieldBlob wall)
            {
                float2 local = (world - wall.OriginWorld) / wall.CellSize;
                return (int2)math.floor(local);
            }

            private static bool IsInWallBounds(int2 grid, ref WallFieldBlob wall)
            {
                return grid.x >= 0 && grid.y >= 0 && grid.x < wall.Width && grid.y < wall.Height;
            }

            private float2 ProjectToNearestWalkable(int2 blockedCell, float2 currentPos)
            {
                int maxR = ProjectionRadius;
                float2 best = currentPos;
                float bestDistSq = float.MaxValue;
                bool found = false;

                for (int r = 1; r <= maxR + 1; r++)
                {
                    int minY = blockedCell.y - r;
                    int maxY = blockedCell.y + r;
                    int minX = blockedCell.x - r;
                    int maxX = blockedCell.x + r;

                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            int2 c = new int2(x, y);
                            if (!Map.IsInMap(c))
                            {
                                continue;
                            }

                            int idx = Map.ToIndex(c);
                            if (!Walkable[idx].IsWalkable)
                            {
                                continue;
                            }

                            float2 candidate = ClosestPointInsideCell(c, currentPos);
                            float d2 = math.lengthsq(candidate - currentPos);
                            if (d2 < bestDistSq)
                            {
                                bestDistSq = d2;
                                best = candidate;
                                found = true;
                            }
                        }
                    }

                    if (found)
                    {
                        return best;
                    }
                }

                return best;
            }

            private float2 ClosestPointInsideCell(int2 cell, float2 worldPos)
            {
                float tileSize = Map.TileSize;
                float2 min = Map.Origin + (new float2(cell.x, cell.y) * tileSize);
                float2 max = min + new float2(tileSize, tileSize);
                float inset = math.max(0.0001f, tileSize * ProjectionInsetFactor);
                float2 minInset = min + inset;
                float2 maxInset = max - inset;
                return math.clamp(worldPos, minInset, maxInset);
            }
        }
    }
}
