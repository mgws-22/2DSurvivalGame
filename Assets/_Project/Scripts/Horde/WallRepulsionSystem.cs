using Project.Map;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(HordeSeparationSystem))]
    public partial struct WallRepulsionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ZombieTag>();
            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<MapWalkableCell>();
            state.RequireForUpdate<WallFieldSingleton>();

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<WallRepulsionConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity e = state.EntityManager.CreateEntity(typeof(WallRepulsionConfig));
                state.EntityManager.SetComponentData(e, new WallRepulsionConfig
                {
                    UnitRadiusWorld = 0.05f,
                    WallPushStrength = 1f,
                    MaxWallPushPerFrame = 0.15f,
                    ProjectionSearchRadiusCells = 1
                });
            }
        }

        public void OnUpdate(ref SystemState state)
        {
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
                MaxPush = math.max(0f, config.MaxWallPushPerFrame),
                ProjectionRadius = math.clamp(config.ProjectionSearchRadiusCells, 1, 2)
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct WallRepulsionJob : IJobEntity
        {
            public MapRuntimeData Map;
            [ReadOnly] public NativeArray<MapWalkableCell> Walkable;
            [ReadOnly] public BlobAssetReference<WallFieldBlob> Wall;
            public float UnitRadius;
            public float WallPushStrength;
            public float MaxPush;
            public int ProjectionRadius;

            private void Execute(ref LocalTransform transform, in ZombieTag tag)
            {
                float2 pos = transform.Position.xy;
                int2 cell = Map.WorldToGrid(pos);
                if (!Map.IsInMap(cell))
                {
                    return;
                }

                int index = Map.ToIndex(cell);
                if (index < 0 || index >= Walkable.Length)
                {
                    return;
                }

                ref WallFieldBlob wall = ref Wall.Value;
                if (!Walkable[index].IsWalkable)
                {
                    pos = ProjectToNearestWalkable(cell, pos);
                    transform.Position = new float3(pos.x, pos.y, transform.Position.z);
                    return;
                }

                if (index >= wall.Dist.Length || index >= wall.Dir.Length)
                {
                    return;
                }

                float d = wall.Dist[index] == ushort.MaxValue ? float.MaxValue : wall.Dist[index] * wall.CellSize;
                if (d < UnitRadius)
                {
                    byte dir = wall.Dir[index];
                    if (dir < wall.DirLut.Length)
                    {
                        float2 n = wall.DirLut[dir];
                        float push = (UnitRadius - d) * WallPushStrength;
                        push = math.min(push, MaxPush);
                        pos += n * push;
                    }
                }

                int2 nextCell = Map.WorldToGrid(pos);
                if (Map.IsInMap(nextCell))
                {
                    int nextIndex = Map.ToIndex(nextCell);
                    if (nextIndex >= 0 && nextIndex < Walkable.Length && !Walkable[nextIndex].IsWalkable)
                    {
                        pos = ProjectToNearestWalkable(nextCell, pos);
                    }
                }

                transform.Position = new float3(pos.x, pos.y, transform.Position.z);
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

                            float2 center = Map.Origin + ((new float2(c.x + 0.5f, c.y + 0.5f)) * Map.TileSize);
                            float d2 = math.lengthsq(center - currentPos);
                            if (d2 < bestDistSq)
                            {
                                bestDistSq = d2;
                                best = center;
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
        }
    }
}
