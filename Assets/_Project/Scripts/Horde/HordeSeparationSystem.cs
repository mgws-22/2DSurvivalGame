using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(HordePressureFieldSystem))]
    [UpdateAfter(typeof(ZombieSteeringSystem))]
    [UpdateBefore(typeof(HordeHardSeparationSystem))]
    public partial struct HordeSeparationSystem : ISystem
    {
        private EntityQuery _zombieQuery;
        private NativeList<Entity> _entities;
        private NativeList<float2> _positionsA;
        private NativeList<float2> _positionsB;
        private NativeList<float> _moveSpeeds;
        private NativeParallelMultiHashMap<int, int> _cellToIndex;
        private NativeArray<int> _congestionCapHitsPerThread;
        private NativeArray<byte> _rebuildGridFlag;
        private ComponentLookup<LocalTransform> _localTransformLookup;

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ZombieTag>(),
                ComponentType.ReadOnly<ZombieMoveSpeed>(),
                ComponentType.ReadWrite<LocalTransform>());

            _entities = new NativeList<Entity>(1024, Allocator.Persistent);
            _positionsA = new NativeList<float2>(1024, Allocator.Persistent);
            _positionsB = new NativeList<float2>(1024, Allocator.Persistent);
            _moveSpeeds = new NativeList<float>(1024, Allocator.Persistent);
            _cellToIndex = new NativeParallelMultiHashMap<int, int>(4096, Allocator.Persistent);
            _congestionCapHitsPerThread = new NativeArray<int>(math.max(1, JobsUtility.MaxJobThreadCount), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _rebuildGridFlag = new NativeArray<byte>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _localTransformLookup = state.GetComponentLookup<LocalTransform>(false);

            state.RequireForUpdate(_zombieQuery);
            state.RequireForUpdate<HordeSeparationConfig>();

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeSeparationConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity configEntity = state.EntityManager.CreateEntity(typeof(HordeSeparationConfig));
                state.EntityManager.SetComponentData(configEntity, new HordeSeparationConfig
                {
                    Radius = 0.20f,
                    CellSizeFactor = 1.25f,
                    InfluenceRadiusFactor = 2.00f,
                    SeparationStrength = 1.00f,
                    MaxPushPerFrame = 2.2f,
                    MaxNeighbors = 32,
                    Iterations = 3,
                    RebuildGridWhenCongested = 0,
                    CongestionCapHitFractionThreshold = 0.10f
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

            if (_moveSpeeds.IsCreated)
            {
                _moveSpeeds.Dispose();
            }

            if (_cellToIndex.IsCreated)
            {
                _cellToIndex.Dispose();
            }

            if (_congestionCapHitsPerThread.IsCreated)
            {
                _congestionCapHitsPerThread.Dispose();
            }

            if (_rebuildGridFlag.IsCreated)
            {
                _rebuildGridFlag.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            HordeSeparationConfig config = SystemAPI.GetSingleton<HordeSeparationConfig>();
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            int count = _zombieQuery.CalculateEntityCount();
            if (count <= 1)
            {
                return;
            }

            float radius = math.max(0.001f, config.Radius);
            float minDist = radius * 2f;
            float cellSize = minDist * math.max(0.5f, config.CellSizeFactor);
            float invCellSize = 1f / cellSize;
            float minDistSq = minDist * minDist;
            float influenceRadius = minDist * math.max(1f, config.InfluenceRadiusFactor);
            float influenceRadiusSq = influenceRadius * influenceRadius;
            float separationStrength = math.clamp(config.SeparationStrength, 0f, 8f);
            float maxPushThisFrame = math.max(0f, config.MaxPushPerFrame) * deltaTime;
            int maxNeighbors = math.clamp(config.MaxNeighbors, 4, 64);
            int iterations = math.clamp(config.Iterations, 1, 8);
            byte rebuildGridWhenCongested = config.RebuildGridWhenCongested != 0 ? (byte)1 : (byte)0;
            float congestionCapHitFractionThreshold = math.clamp(config.CongestionCapHitFractionThreshold, 0f, 1f);

            _localTransformLookup.Update(ref state);

            EnsureCapacity(count);
            _entities.ResizeUninitialized(count);
            _positionsA.ResizeUninitialized(count);
            _positionsB.ResizeUninitialized(count);
            _moveSpeeds.ResizeUninitialized(count);

            int requiredHashCapacity = math.max(1024, count * 12);
            if (_cellToIndex.Capacity < requiredHashCapacity)
            {
                _cellToIndex.Capacity = requiredHashCapacity;
            }

            GatherZombieSnapshotJob gatherJob = new GatherZombieSnapshotJob
            {
                Entities = _entities.AsArray(),
                Positions = _positionsA.AsArray(),
                MoveSpeeds = _moveSpeeds.AsArray()
            };
            state.Dependency = gatherJob.ScheduleParallel(state.Dependency);

            NativeArray<float2> sourcePositions = _positionsA.AsArray();
            NativeArray<float2> targetPositions = _positionsB.AsArray();

            ClearSpatialGridJob clearGridJob = new ClearSpatialGridJob
            {
                Grid = _cellToIndex
            };
            state.Dependency = clearGridJob.Schedule(state.Dependency);

            BuildSpatialGridJob buildGridJob = new BuildSpatialGridJob
            {
                Positions = sourcePositions,
                Grid = _cellToIndex.AsParallelWriter(),
                InvCellSize = invCellSize
            };
            state.Dependency = buildGridJob.Schedule(count, 128, state.Dependency);

            bool trackCongestionFallback = rebuildGridWhenCongested != 0 && iterations > 1;
            if (trackCongestionFallback)
            {
                ClearCongestionTrackingJob clearCongestionJob = new ClearCongestionTrackingJob
                {
                    CapHitsPerThread = _congestionCapHitsPerThread,
                    RebuildFlag = _rebuildGridFlag
                };
                state.Dependency = clearCongestionJob.Schedule(state.Dependency);
            }

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                SeparateJob separateJob = new SeparateJob
                {
                    Positions = sourcePositions,
                    Grid = _cellToIndex,
                    CorrectedPositions = targetPositions,
                    InvCellSize = invCellSize,
                    MinDistSq = minDistSq,
                    MinDist = minDist,
                    InfluenceRadiusSq = influenceRadiusSq,
                    MaxPush = maxPushThisFrame,
                    SeparationStrength = separationStrength,
                    MaxNeighbors = maxNeighbors,
                    MoveSpeeds = _moveSpeeds.AsArray(),
                    DeltaTime = deltaTime,
                    Iterations = iterations,
                    CurrentIteration = iteration,
                    TrackCongestionCapHits = (byte)((trackCongestionFallback && iteration == 0) ? 1 : 0),
                    CongestionCapHitsPerThread = _congestionCapHitsPerThread
                };
                state.Dependency = separateJob.Schedule(count, 128, state.Dependency);

                NativeArray<float2> swap = sourcePositions;
                sourcePositions = targetPositions;
                targetPositions = swap;

                if (trackCongestionFallback && iteration == 0)
                {
                    DecideGridRebuildFromCongestionJob decideGridRebuildJob = new DecideGridRebuildFromCongestionJob
                    {
                        CapHitsPerThread = _congestionCapHitsPerThread,
                        RebuildFlag = _rebuildGridFlag,
                        Enabled = rebuildGridWhenCongested,
                        UnitCount = count,
                        CapHitFractionThreshold = congestionCapHitFractionThreshold
                    };
                    state.Dependency = decideGridRebuildJob.Schedule(state.Dependency);

                    ConditionalClearSpatialGridJob conditionalClearGridJob = new ConditionalClearSpatialGridJob
                    {
                        Grid = _cellToIndex,
                        RebuildFlag = _rebuildGridFlag
                    };
                    state.Dependency = conditionalClearGridJob.Schedule(state.Dependency);

                    ConditionalBuildSpatialGridJob conditionalBuildGridJob = new ConditionalBuildSpatialGridJob
                    {
                        Positions = sourcePositions,
                        Grid = _cellToIndex.AsParallelWriter(),
                        InvCellSize = invCellSize,
                        RebuildFlag = _rebuildGridFlag
                    };
                    state.Dependency = conditionalBuildGridJob.Schedule(count, 128, state.Dependency);
                }
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

            if (_moveSpeeds.Capacity < count)
            {
                _moveSpeeds.Capacity = math.ceilpow2(count);
            }
        }

        [BurstCompile]
        private partial struct GatherZombieSnapshotJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public NativeArray<Entity> Entities;
            [NativeDisableParallelForRestriction] public NativeArray<float2> Positions;
            [NativeDisableParallelForRestriction] public NativeArray<float> MoveSpeeds;

            private void Execute(Entity entity, [EntityIndexInQuery] int index, in ZombieTag tag, in LocalTransform transform, in ZombieMoveSpeed moveSpeed)
            {
                Entities[index] = entity;
                Positions[index] = transform.Position.xy;
                MoveSpeeds[index] = math.max(0f, moveSpeed.Value);
            }
        }

        [BurstCompile]
        private struct ClearSpatialGridJob : IJob
        {
            public NativeParallelMultiHashMap<int, int> Grid;

            public void Execute()
            {
                Grid.Clear();
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
        private struct ConditionalClearSpatialGridJob : IJob
        {
            public NativeParallelMultiHashMap<int, int> Grid;
            [ReadOnly] public NativeArray<byte> RebuildFlag;

            public void Execute()
            {
                if (RebuildFlag[0] == 0)
                {
                    return;
                }

                Grid.Clear();
            }
        }

        [BurstCompile]
        private struct ConditionalBuildSpatialGridJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Positions;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Grid;
            public float InvCellSize;
            [ReadOnly] public NativeArray<byte> RebuildFlag;

            public void Execute(int index)
            {
                if (RebuildFlag[0] == 0)
                {
                    return;
                }

                int2 cell = (int2)math.floor(Positions[index] * InvCellSize);
                Grid.Add(HashCell(cell.x, cell.y), index);
            }
        }

        [BurstCompile]
        private struct ClearCongestionTrackingJob : IJob
        {
            public NativeArray<int> CapHitsPerThread;
            public NativeArray<byte> RebuildFlag;

            public void Execute()
            {
                for (int i = 0; i < CapHitsPerThread.Length; i++)
                {
                    CapHitsPerThread[i] = 0;
                }

                RebuildFlag[0] = 0;
            }
        }

        [BurstCompile]
        private struct DecideGridRebuildFromCongestionJob : IJob
        {
            [ReadOnly] public NativeArray<int> CapHitsPerThread;
            public NativeArray<byte> RebuildFlag;
            public byte Enabled;
            public int UnitCount;
            public float CapHitFractionThreshold;

            public void Execute()
            {
                if (Enabled == 0 || UnitCount <= 0)
                {
                    RebuildFlag[0] = 0;
                    return;
                }

                int capHits = 0;
                for (int i = 0; i < CapHitsPerThread.Length; i++)
                {
                    capHits += CapHitsPerThread[i];
                }

                float fraction = (float)capHits / math.max(1, UnitCount);
                RebuildFlag[0] = fraction >= CapHitFractionThreshold ? (byte)1 : (byte)0;
            }
        }

        [BurstCompile]
        private struct SeparateJob : IJobParallelFor
        {
            private const float ZeroDistanceSq = 1e-12f;
            private const float Diagonal = 0.70710677f;

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
            [ReadOnly] public NativeArray<float> MoveSpeeds;
            public float DeltaTime;
            public int Iterations;
            public int CurrentIteration;
            public byte TrackCongestionCapHits;
            [NativeDisableParallelForRestriction] public NativeArray<int> CongestionCapHitsPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;

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
                            if (distSq > InfluenceRadiusSq)
                            {
                                continue;
                            }

                            if (distSq < MinDistSq)
                            {
                                float2 normal;
                                float dist;
                                if (distSq <= ZeroDistanceSq)
                                {
                                    normal = DeterministicNormal(index, neighborIndex, CurrentIteration);
                                    dist = 0f;
                                }
                                else
                                {
                                    float invDist = math.rsqrt(distSq);
                                    dist = distSq * invDist;
                                    normal = delta * invDist;
                                }

                                float push = MinDist - dist;
                                correction += normal * push;
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
                    float2 softDelta = correction * SeparationStrength;
                    float softLenSq = math.lengthsq(softDelta);
                    float maxStepBySpeed = MoveSpeeds[index] * DeltaTime;
                    float perIterationSpeedCap = maxStepBySpeed / math.max(1, Iterations);
                    float perIterationConfigCap = MaxPush / math.max(1, Iterations);
                    float effectiveMaxPush = math.min(perIterationConfigCap, perIterationSpeedCap);
                    float maxPushSq = effectiveMaxPush * effectiveMaxPush;
                    if (softLenSq > maxPushSq && softLenSq > 0.000001f)
                    {
                        softDelta *= effectiveMaxPush * math.rsqrt(softLenSq);
                    }

                    pos += softDelta;
                }

                if (TrackCongestionCapHits != 0 && reachedCap)
                {
                    int workerIndex = math.clamp(ThreadIndex - 1, 0, CongestionCapHitsPerThread.Length - 1);
                    CongestionCapHitsPerThread[workerIndex] = CongestionCapHitsPerThread[workerIndex] + 1;
                }

                CorrectedPositions[index] = pos;
            }

            private static float2 DeterministicNormal(int index, int neighborIndex, int iteration)
            {
                uint h = (uint)index * 0x9E3779B9u;
                h ^= ((uint)neighborIndex * 0x85EBCA6Bu);
                h ^= ((uint)iteration * 0xC2B2AE35u);
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;

                switch ((int)(h & 7u))
                {
                    case 0: return new float2(1f, 0f);
                    case 1: return new float2(Diagonal, Diagonal);
                    case 2: return new float2(0f, 1f);
                    case 3: return new float2(-Diagonal, Diagonal);
                    case 4: return new float2(-1f, 0f);
                    case 5: return new float2(-Diagonal, -Diagonal);
                    case 6: return new float2(0f, -1f);
                    default: return new float2(Diagonal, -Diagonal);
                }
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
