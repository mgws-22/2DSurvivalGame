using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ZombieSteeringSystem))]
    public partial struct HordeSeparationSystem : ISystem
    {
        private EntityQuery _zombieQuery;
        private NativeList<Entity> _entities;
        private NativeList<float2> _positionsA;
        private NativeList<float2> _positionsB;
        private NativeParallelMultiHashMap<int, int> _cellToIndex;
        private ComponentLookup<LocalTransform> _localTransformLookup;

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ZombieTag>(),
                ComponentType.ReadWrite<LocalTransform>());

            _entities = new NativeList<Entity>(1024, Allocator.Persistent);
            _positionsA = new NativeList<float2>(1024, Allocator.Persistent);
            _positionsB = new NativeList<float2>(1024, Allocator.Persistent);
            _cellToIndex = new NativeParallelMultiHashMap<int, int>(4096, Allocator.Persistent);
            _localTransformLookup = state.GetComponentLookup<LocalTransform>(false);

            state.RequireForUpdate(_zombieQuery);
            state.RequireForUpdate<HordeSeparationConfig>();

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeSeparationConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity configEntity = state.EntityManager.CreateEntity(typeof(HordeSeparationConfig));
                state.EntityManager.SetComponentData(configEntity, new HordeSeparationConfig
                {
                    Radius = 0.1f,
                    CellSizeFactor = 1.25f,
                    InfluenceRadiusFactor = 1.5f,
                    SeparationStrength = 0.4f,
                    MaxPushPerFrame = 0.9f,
                    MaxNeighbors = 24,
                    Iterations = 1
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_entities.IsCreated)
            {
                _entities.Dispose();
            }

            if (_positionsA.IsCreated)
            {
                _positionsA.Dispose();
            }

            if (_positionsB.IsCreated)
            {
                _positionsB.Dispose();
            }

            if (_cellToIndex.IsCreated)
            {
                _cellToIndex.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            int count = _zombieQuery.CalculateEntityCount();
            if (count <= 1)
            {
                return;
            }

            HordeSeparationConfig config = SystemAPI.GetSingleton<HordeSeparationConfig>();
            float radius = math.max(0.001f, config.Radius);
            float minDist = radius * 2f;
            float cellSize = minDist * math.max(0.5f, config.CellSizeFactor);
            float invCellSize = 1f / cellSize;
            float minDistSq = minDist * minDist;
            float influenceRadius = minDist * math.max(1f, config.InfluenceRadiusFactor);
            float influenceRadiusSq = influenceRadius * influenceRadius;
            float separationStrength = math.saturate(config.SeparationStrength);
            float maxPush = math.max(0f, config.MaxPushPerFrame);
            int maxNeighbors = math.clamp(config.MaxNeighbors, 4, 64);
            int iterations = math.clamp(config.Iterations, 1, 2);
            _localTransformLookup.Update(ref state);

            EnsureCapacity(count);
            _entities.ResizeUninitialized(count);
            _positionsA.ResizeUninitialized(count);
            _positionsB.ResizeUninitialized(count);

            int requiredHashCapacity = math.max(1024, count * 12);
            if (_cellToIndex.Capacity < requiredHashCapacity)
            {
                _cellToIndex.Capacity = requiredHashCapacity;
            }

            GatherZombieSnapshotJob gatherJob = new GatherZombieSnapshotJob
            {
                Entities = _entities.AsArray(),
                Positions = _positionsA.AsArray()
            };
            state.Dependency = gatherJob.ScheduleParallel(state.Dependency);

            NativeArray<float2> sourcePositions = _positionsA.AsArray();
            NativeArray<float2> targetPositions = _positionsB.AsArray();

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                _cellToIndex.Clear();

                BuildSpatialGridJob buildGridJob = new BuildSpatialGridJob
                {
                    Positions = sourcePositions,
                    Grid = _cellToIndex.AsParallelWriter(),
                    InvCellSize = invCellSize
                };
                state.Dependency = buildGridJob.Schedule(count, 128, state.Dependency);

                SeparateJob separateJob = new SeparateJob
                {
                    Positions = sourcePositions,
                    Grid = _cellToIndex,
                    CorrectedPositions = targetPositions,
                    InvCellSize = invCellSize,
                    MinDistSq = minDistSq,
                    MinDist = minDist,
                    InfluenceRadiusSq = influenceRadiusSq,
                    MaxPush = maxPush,
                    SeparationStrength = separationStrength,
                    MaxNeighbors = maxNeighbors
                };
                state.Dependency = separateJob.Schedule(count, 128, state.Dependency);

                NativeArray<float2> swap = sourcePositions;
                sourcePositions = targetPositions;
                targetPositions = swap;
            }

            ApplyPositionsJob applyJob = new ApplyPositionsJob
            {
                Entities = _entities.AsArray(),
                Positions = sourcePositions,
                Transforms = _localTransformLookup
            };
            state.Dependency = applyJob.Schedule(count, 128, state.Dependency);
        }

        private void EnsureCapacity(int count)
        {
            if (_entities.Capacity < count)
            {
                _entities.Capacity = math.ceilpow2(count);
            }

            if (_positionsA.Capacity < count)
            {
                _positionsA.Capacity = math.ceilpow2(count);
            }

            if (_positionsB.Capacity < count)
            {
                _positionsB.Capacity = math.ceilpow2(count);
            }
        }

        [BurstCompile]
        private partial struct GatherZombieSnapshotJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public NativeArray<Entity> Entities;
            [NativeDisableParallelForRestriction] public NativeArray<float2> Positions;

            private void Execute(Entity entity, [EntityIndexInQuery] int index, in ZombieTag tag, in LocalTransform transform)
            {
                Entities[index] = entity;
                Positions[index] = transform.Position.xy;
            }
        }

        [BurstCompile]
        private struct BuildSpatialGridJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Positions;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Grid;
            public float InvCellSize;

            public void Execute(int index)
            {
                int2 cell = (int2)math.floor(Positions[index] * InvCellSize);
                Grid.Add(HashCell(cell.x, cell.y), index);
            }
        }

        [BurstCompile]
        private struct SeparateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Positions;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
            [NativeDisableParallelForRestriction] public NativeArray<float2> CorrectedPositions;
            public float InvCellSize;
            public float MinDistSq;
            public float MinDist;
            public float InfluenceRadiusSq;
            public float MaxPush;
            public float SeparationStrength;
            public int MaxNeighbors;

            public void Execute(int index)
            {
                float2 pos = Positions[index];
                int2 cell = (int2)math.floor(pos * InvCellSize);
                float2 correction = float2.zero;
                int processed = 0;
                bool reachedCap = false;

                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int key = HashCell(cell.x + ox, cell.y + oy);
                        if (!Grid.TryGetFirstValue(key, out int neighborIndex, out NativeParallelMultiHashMapIterator<int> it))
                        {
                            continue;
                        }

                        do
                        {
                            if (neighborIndex == index)
                            {
                                continue;
                            }

                            float2 delta = pos - Positions[neighborIndex];
                            float distSq = math.lengthsq(delta);
                            if (distSq <= 0.000001f || distSq > InfluenceRadiusSq)
                            {
                                continue;
                            }

                            if (distSq < MinDistSq)
                            {
                                float invDist = math.rsqrt(distSq);
                                float dist = distSq * invDist;
                                float push = MinDist - dist;
                                correction += delta * (invDist * push);
                                processed++;
                                if (processed >= MaxNeighbors)
                                {
                                    reachedCap = true;
                                    break;
                                }
                            }
                        }
                        while (Grid.TryGetNextValue(out neighborIndex, ref it));

                        if (reachedCap)
                        {
                            break;
                        }
                    }

                    if (reachedCap)
                    {
                        break;
                    }
                }

                float corrLenSq = math.lengthsq(correction);
                if (corrLenSq > 0.000001f)
                {
                    float maxPushSq = MaxPush * MaxPush;
                    if (corrLenSq > maxPushSq)
                    {
                        correction *= MaxPush * math.rsqrt(corrLenSq);
                    }

                    pos += correction * SeparationStrength;
                }

                CorrectedPositions[index] = pos;
            }
        }

        [BurstCompile]
        private struct ApplyPositionsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<float2> Positions;
            [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> Transforms;

            public void Execute(int index)
            {
                Entity entity = Entities[index];
                if (!Transforms.HasComponent(entity))
                {
                    return;
                }

                LocalTransform transform = Transforms[entity];
                float2 p = Positions[index];
                transform.Position = new float3(p.x, p.y, transform.Position.z);
                Transforms[entity] = transform;
            }
        }

        [BurstCompile]
        private static int HashCell(int x, int y)
        {
            return (x * 73856093) ^ (y * 19349663);
        }
    }
}
