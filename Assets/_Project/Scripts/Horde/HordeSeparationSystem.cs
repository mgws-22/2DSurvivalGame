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
    [UpdateAfter(typeof(HordePressureFieldSystem))]
    [UpdateAfter(typeof(ZombieSteeringSystem))]
    [UpdateBefore(typeof(HordeHardSeparationSystem))]
    public partial struct HordeSeparationSystem : ISystem
    {
        private static bool s_loggedRuntimeDiagnostics;
        private EntityQuery _zombieQuery;
        private NativeList<Entity> _entities;
        private NativeList<float2> _positionsA;
        private NativeList<float2> _positionsB;
        private NativeList<float> _moveSpeeds;
        private NativeParallelMultiHashMap<int, int> _cellToIndex;
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
            _localTransformLookup = state.GetComponentLookup<LocalTransform>(false);

            state.RequireForUpdate(_zombieQuery);
            state.RequireForUpdate<HordeSeparationConfig>();

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeSeparationConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity configEntity = state.EntityManager.CreateEntity(typeof(HordeSeparationConfig));
                state.EntityManager.SetComponentData(configEntity, new HordeSeparationConfig
                {
                    // Viktigt: detta måste matcha din sprite/world scale (halva diametern)
                    Radius = 0.10f,

                    CellSizeFactor = 1.25f,
                    InfluenceRadiusFactor = 1.75f,

                    // SeparationStrength: håll modest för att undvika jitter,
                    // låt iterations + caps göra jobbet.
                    SeparationStrength = 1.0f,

                    // Se till att separation inte är "för snål" i trängsel.
                    // Om du har global speed clamp (moveSpeed*dt) kan du sätta den högre.
                    MaxPushPerFrame = 0.25f,

                    MaxNeighbors = 24,

                    // Punktmål + trängsel => 2 iterationer är ofta nyckeln
                    Iterations = 2
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
        }

        public void OnUpdate(ref SystemState state)
        {
            HordeSeparationConfig config = SystemAPI.GetSingleton<HordeSeparationConfig>();
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!s_loggedRuntimeDiagnostics)
            {
                bool hasPressureConfig = SystemAPI.TryGetSingleton(out HordePressureConfig pressureConfig);
                bool hasHardConfig = SystemAPI.TryGetSingleton(out HordeHardSeparationConfig hardConfig);
                bool hasWallConfig = SystemAPI.TryGetSingleton(out WallRepulsionConfig wallConfig);
                float referenceMoveSpeed = 1f;
                float maxStep = referenceMoveSpeed * deltaTime;
                float pressureMaxPushPerFrame = hasPressureConfig ? math.max(0f, pressureConfig.MaxPushPerFrame) : 0f;
                float pressureSpeedFractionCap = hasPressureConfig ? math.clamp(pressureConfig.SpeedFractionCap, 0f, 1f) : 0f;
                float pressureMaxFromConfig = pressureMaxPushPerFrame * deltaTime;
                float pressureMaxFromSpeed = maxStep * pressureSpeedFractionCap;
                float pressureMaxThisFrame = hasPressureConfig ? math.min(pressureMaxFromConfig, pressureMaxFromSpeed) : 0f;
                float separationMaxThisFrame = math.max(0f, config.MaxPushPerFrame) * deltaTime;
                float wallMaxThisFrame = hasWallConfig ? math.max(0f, wallConfig.MaxWallPushPerFrame) * deltaTime : 0f;
                string order = "ZombieSteeringSystem -> HordePressureFieldSystem -> HordeSeparationSystem -> HordeHardSeparationSystem -> WallRepulsionSystem";
                UnityEngine.Debug.Log(
                    $"[HordeRuntimeDiag] PressureEnabled={(hasPressureConfig ? pressureConfig.Enabled : (byte)0)} " +
                    $"DisablePairwiseWhenPressure={(hasPressureConfig ? pressureConfig.DisablePairwiseSeparationWhenPressureEnabled : (byte)0)} " +
                    $"PressureMaxPushPerFrame={pressureMaxPushPerFrame:F3} PressureSpeedFractionCap={pressureSpeedFractionCap:F2} " +
                    $"SoftEnabled=1 SoftMaxNeighbors={config.MaxNeighbors} SoftIterations={config.Iterations} SoftMaxPushPerFrame={config.MaxPushPerFrame:F3} " +
                    $"HardEnabled={(hasHardConfig ? hardConfig.Enabled : (byte)0)} HardMaxNeighbors={(hasHardConfig ? hardConfig.MaxNeighbors : 0)} HardIterations={(hasHardConfig ? hardConfig.Iterations : 0)} " +
                    $"dt={deltaTime:F4} RefMoveSpeed={referenceMoveSpeed:F2} RefMaxStep={maxStep:F4} " +
                    $"PressureConfigBudgetThisFrame={pressureMaxFromConfig:F4} PressureSpeedBudgetThisFrame={pressureMaxFromSpeed:F4} PressureEffectiveCapThisFrame={pressureMaxThisFrame:F4} " +
                    $"SeparationMaxThisFrame={separationMaxThisFrame:F4} WallMaxThisFrame={wallMaxThisFrame:F4} " +
                    $"Order={order}");
                s_loggedRuntimeDiagnostics = true;
            }
#endif

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
            float separationStrength = math.saturate(config.SeparationStrength);
            float maxPushThisFrame = math.max(0f, config.MaxPushPerFrame) * deltaTime;
            int maxNeighbors = math.clamp(config.MaxNeighbors, 4, 64);
            int iterations = math.clamp(config.Iterations, 1, 2);
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

            for (int iteration = 0; iteration < iterations; iteration++)
            {
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
                    CurrentIteration = iteration
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
