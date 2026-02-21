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
    [UpdateAfter(typeof(WallRepulsionSystem))]
    public partial struct HordeTuningQuickMetricsSystem : ISystem
    {
        private static bool s_loggedConfigOnce;

        private EntityQuery _zombieQuery;
        private Entity _metricsEntity;
        private int _workerCount;
        private int _frameCounter;
        private float _elapsedSinceTick;
        private bool _pendingMetricsLog;

        private NativeList<Entity> _entities;
        private NativeList<float2> _positions;
        private NativeList<float> _moveSpeeds;
        private NativeParallelMultiHashMap<int, int> _cellToIndex;
        private NativeParallelHashMap<Entity, float2> _previousSampledPositions;
        private NativeArray<int> _sampledPerThread;
        private NativeArray<int> _overlapPerThread;
        private NativeArray<int> _jamPerThread;
        private ComponentLookup<HordeTuningQuickMetrics> _metricsLookup;

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ZombieTag>(),
                ComponentType.ReadOnly<ZombieMoveSpeed>(),
                ComponentType.ReadOnly<LocalTransform>());

            _workerCount = math.max(1, JobsUtility.MaxJobThreadCount);
            _entities = new NativeList<Entity>(1024, Allocator.Persistent);
            _positions = new NativeList<float2>(1024, Allocator.Persistent);
            _moveSpeeds = new NativeList<float>(1024, Allocator.Persistent);
            _cellToIndex = new NativeParallelMultiHashMap<int, int>(4096, Allocator.Persistent);
            _previousSampledPositions = new NativeParallelHashMap<Entity, float2>(8192, Allocator.Persistent);
            _sampledPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _overlapPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _jamPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _metricsLookup = state.GetComponentLookup<HordeTuningQuickMetrics>(false);

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeTuningQuickConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                const int defaultEnabled = 1;
#else
                const int defaultEnabled = 0;
#endif
                Entity cfgEntity = state.EntityManager.CreateEntity(typeof(HordeTuningQuickConfig));
                state.EntityManager.SetComponentData(cfgEntity, new HordeTuningQuickConfig
                {
                    Enabled = defaultEnabled,
                    LogEveryNFrames = 60,
                    SampleStride = 32
                });
            }

            EntityQuery metricsQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeTuningQuickMetrics>());
            if (metricsQuery.IsEmptyIgnoreFilter)
            {
                _metricsEntity = state.EntityManager.CreateEntity(typeof(HordeTuningQuickMetrics));
                state.EntityManager.SetComponentData(_metricsEntity, default(HordeTuningQuickMetrics));
            }
            else
            {
                _metricsEntity = metricsQuery.GetSingletonEntity();
            }

            state.RequireForUpdate(_zombieQuery);
            state.RequireForUpdate<HordeTuningQuickConfig>();
            state.RequireForUpdate<HordeSeparationConfig>();
            state.RequireForUpdate<HordePressureConfig>();
            state.RequireForUpdate<HordeTuningQuickMetrics>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_entities.IsCreated)
            {
                _entities.Dispose();
            }

            if (_positions.IsCreated)
            {
                _positions.Dispose();
            }

            if (_moveSpeeds.IsCreated)
            {
                _moveSpeeds.Dispose();
            }

            if (_cellToIndex.IsCreated)
            {
                _cellToIndex.Dispose();
            }

            if (_previousSampledPositions.IsCreated)
            {
                _previousSampledPositions.Dispose();
            }

            if (_sampledPerThread.IsCreated)
            {
                _sampledPerThread.Dispose();
            }

            if (_overlapPerThread.IsCreated)
            {
                _overlapPerThread.Dispose();
            }

            if (_jamPerThread.IsCreated)
            {
                _jamPerThread.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            HordeTuningQuickConfig quickConfig = SystemAPI.GetSingleton<HordeTuningQuickConfig>();
            if (quickConfig.Enabled == 0)
            {
                return;
            }

            HordeSeparationConfig separationConfig = SystemAPI.GetSingleton<HordeSeparationConfig>();
            HordePressureConfig pressureConfig = SystemAPI.GetSingleton<HordePressureConfig>();
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            _elapsedSinceTick += deltaTime;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!s_loggedConfigOnce)
            {
                UnityEngine.Debug.Log(
                    $"[HordeTune] cfg targetUnits={pressureConfig.TargetUnitsPerCell:F2} pressureSpeedFractionCap={pressureConfig.SpeedFractionCap:F2} " +
                    $"sepMaxPushPerFrame={separationConfig.MaxPushPerFrame:F2} sepIterations={separationConfig.Iterations} radius={separationConfig.Radius:F3} " +
                    $"logEveryFrames={quickConfig.LogEveryNFrames} sampleStride={quickConfig.SampleStride}");
                s_loggedConfigOnce = true;
            }

            if (_pendingMetricsLog)
            {
                HordeTuningQuickMetrics metrics = SystemAPI.GetSingleton<HordeTuningQuickMetrics>();
                float dtWindow = math.max(0f, metrics.Dt);
                float referenceMoveSpeed = 1f;
                float refMaxStep = referenceMoveSpeed * dtWindow;
                float pressureConfigBudget = math.max(0f, pressureConfig.MaxPushPerFrame) * dtWindow;
                float pressureSpeedBudget = refMaxStep * math.clamp(pressureConfig.SpeedFractionCap, 0f, 1f);
                float pressureEffectiveCap = math.min(pressureConfigBudget, pressureSpeedBudget);
                float separationCap = math.min(math.max(0f, separationConfig.MaxPushPerFrame) * dtWindow, refMaxStep);

                float overlapPct = metrics.Sampled > 0 ? (100f * metrics.OverlapHits / metrics.Sampled) : 0f;
                float jamPct = metrics.Sampled > 0 ? (100f * metrics.JamHits / metrics.Sampled) : 0f;

                UnityEngine.Debug.Log(
                    $"[HordeTune] dt={dtWindow:F4} sampled={metrics.Sampled} overlap={overlapPct:F1}% jam={jamPct:F1}% " +
                    $"sepCap={separationCap:F4} pressureCap={pressureEffectiveCap:F4} radius={math.max(0.001f, separationConfig.Radius):F3}");

                _pendingMetricsLog = false;
            }
#endif

            _frameCounter++;
            int logEveryNFrames = math.max(1, quickConfig.LogEveryNFrames);
            if ((_frameCounter % logEveryNFrames) != 0)
            {
                return;
            }

            int sampleStride = math.max(1, quickConfig.SampleStride);
            int count = _zombieQuery.CalculateEntityCount();
            if (count <= 1)
            {
                _elapsedSinceTick = 0f;
                return;
            }

            float radius = math.max(0.001f, separationConfig.Radius);
            float minDist = radius * 2f;
            float minDistSq = minDist * minDist;
            float cellSize = minDist * math.max(0.5f, separationConfig.CellSizeFactor);
            float invCellSize = 1f / cellSize;
            float influenceRadius = minDist * math.max(1f, separationConfig.InfluenceRadiusFactor);
            float influenceRadiusSq = influenceRadius * influenceRadius;
            int maxNeighbors = math.clamp(separationConfig.MaxNeighbors, 4, 64);
            float targetUnitsPerCell = math.max(0f, pressureConfig.TargetUnitsPerCell);
            float dtWindowForSpeed = math.max(deltaTime, _elapsedSinceTick);
            _elapsedSinceTick = 0f;

            EnsureCapacity(count, sampleStride);
            _entities.ResizeUninitialized(count);
            _positions.ResizeUninitialized(count);
            _moveSpeeds.ResizeUninitialized(count);

            JobHandle dependency = state.Dependency;

            GatherZombieSnapshotJob gatherJob = new GatherZombieSnapshotJob
            {
                Entities = _entities.AsArray(),
                Positions = _positions.AsArray(),
                MoveSpeeds = _moveSpeeds.AsArray()
            };
            dependency = gatherJob.ScheduleParallel(dependency);

            ClearSpatialGridJob clearGridJob = new ClearSpatialGridJob
            {
                Grid = _cellToIndex
            };
            dependency = clearGridJob.Schedule(dependency);

            BuildSpatialGridJob buildGridJob = new BuildSpatialGridJob
            {
                Positions = _positions.AsArray(),
                Grid = _cellToIndex.AsParallelWriter(),
                InvCellSize = invCellSize
            };
            dependency = buildGridJob.Schedule(count, 128, dependency);

            ClearThreadCountersJob clearCountersJob = new ClearThreadCountersJob
            {
                Sampled = _sampledPerThread,
                Overlap = _overlapPerThread,
                Jam = _jamPerThread
            };
            dependency = clearCountersJob.Schedule(_workerCount, 64, dependency);

            EvaluateQuickMetricsJob evaluateJob = new EvaluateQuickMetricsJob
            {
                Entities = _entities.AsArray(),
                Positions = _positions.AsArray(),
                MoveSpeeds = _moveSpeeds.AsArray(),
                Grid = _cellToIndex,
                PreviousSampledPositions = _previousSampledPositions,
                SampledPerThread = _sampledPerThread,
                OverlapPerThread = _overlapPerThread,
                JamPerThread = _jamPerThread,
                InvCellSize = invCellSize,
                MinDistSq = minDistSq,
                InfluenceRadiusSq = influenceRadiusSq,
                MaxNeighbors = maxNeighbors,
                SampleStride = sampleStride,
                TargetUnitsPerCell = targetUnitsPerCell,
                DeltaTimeWindow = dtWindowForSpeed,
                SpeedThresholdFactor = 0.2f,
                WorkerCount = _workerCount
            };
            dependency = evaluateJob.Schedule(count, 128, dependency);

            _metricsLookup.Update(ref state);
            ReduceQuickMetricsJob reduceJob = new ReduceQuickMetricsJob
            {
                SampledPerThread = _sampledPerThread,
                OverlapPerThread = _overlapPerThread,
                JamPerThread = _jamPerThread,
                MetricsLookup = _metricsLookup,
                MetricsEntity = _metricsEntity,
                Dt = dtWindowForSpeed,
                WorkerCount = _workerCount
            };
            dependency = reduceJob.Schedule(dependency);

            ClearSampledMapJob clearSampledMapJob = new ClearSampledMapJob
            {
                Map = _previousSampledPositions
            };
            dependency = clearSampledMapJob.Schedule(dependency);

            StoreSampledPositionsJob storeSampledJob = new StoreSampledPositionsJob
            {
                Entities = _entities.AsArray(),
                Positions = _positions.AsArray(),
                SampleStride = sampleStride,
                Writer = _previousSampledPositions.AsParallelWriter()
            };
            dependency = storeSampledJob.Schedule(count, 128, dependency);

            state.Dependency = dependency;
            _pendingMetricsLog = true;
        }

        private void EnsureCapacity(int count, int sampleStride)
        {
            if (_entities.Capacity < count)
            {
                _entities.Capacity = math.ceilpow2(count);
            }

            if (_positions.Capacity < count)
            {
                _positions.Capacity = math.ceilpow2(count);
            }

            if (_moveSpeeds.Capacity < count)
            {
                _moveSpeeds.Capacity = math.ceilpow2(count);
            }

            int requiredGridCapacity = math.max(1024, count * 12);
            if (_cellToIndex.Capacity < requiredGridCapacity)
            {
                _cellToIndex.Capacity = requiredGridCapacity;
            }

            _ = sampleStride;
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
        private struct ClearThreadCountersJob : IJobParallelFor
        {
            public NativeArray<int> Sampled;
            public NativeArray<int> Overlap;
            public NativeArray<int> Jam;

            public void Execute(int index)
            {
                Sampled[index] = 0;
                Overlap[index] = 0;
                Jam[index] = 0;
            }
        }

        [BurstCompile]
        private struct EvaluateQuickMetricsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<float2> Positions;
            [ReadOnly] public NativeArray<float> MoveSpeeds;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
            [ReadOnly] public NativeParallelHashMap<Entity, float2> PreviousSampledPositions;
            [NativeDisableParallelForRestriction] public NativeArray<int> SampledPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> OverlapPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> JamPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;
            public float InvCellSize;
            public float MinDistSq;
            public float InfluenceRadiusSq;
            public int MaxNeighbors;
            public int SampleStride;
            public float TargetUnitsPerCell;
            public float DeltaTimeWindow;
            public float SpeedThresholdFactor;
            public int WorkerCount;

            public void Execute(int index)
            {
                if ((index % SampleStride) != 0)
                {
                    return;
                }

                int workerIndex = math.clamp(ThreadIndex - 1, 0, WorkerCount - 1);
                SampledPerThread[workerIndex] = SampledPerThread[workerIndex] + 1;

                float2 pos = Positions[index];
                int2 cell = (int2)math.floor(pos * InvCellSize);
                int processed = 0;
                int localNeighbors = 0;
                bool overlap = false;
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

                            localNeighbors++;
                            if (distSq < MinDistSq)
                            {
                                overlap = true;
                            }

                            processed++;
                            if (processed >= MaxNeighbors)
                            {
                                reachedCap = true;
                                break;
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

                if (overlap)
                {
                    OverlapPerThread[workerIndex] = OverlapPerThread[workerIndex] + 1;
                }

                bool dense = (1f + localNeighbors) >= TargetUnitsPerCell;
                bool slow = false;
                Entity entity = Entities[index];
                if (PreviousSampledPositions.TryGetValue(entity, out float2 previousPos))
                {
                    float dist = math.distance(pos, previousPos);
                    float speed = dist / math.max(1e-5f, DeltaTimeWindow);
                    slow = speed < (MoveSpeeds[index] * SpeedThresholdFactor);
                }

                if (slow && dense)
                {
                    JamPerThread[workerIndex] = JamPerThread[workerIndex] + 1;
                }
            }
        }

        [BurstCompile]
        private struct ReduceQuickMetricsJob : IJob
        {
            [ReadOnly] public NativeArray<int> SampledPerThread;
            [ReadOnly] public NativeArray<int> OverlapPerThread;
            [ReadOnly] public NativeArray<int> JamPerThread;
            [NativeDisableParallelForRestriction] public ComponentLookup<HordeTuningQuickMetrics> MetricsLookup;
            public Entity MetricsEntity;
            public float Dt;
            public int WorkerCount;

            public void Execute()
            {
                if (!MetricsLookup.HasComponent(MetricsEntity))
                {
                    return;
                }

                int sampled = 0;
                int overlap = 0;
                int jam = 0;
                for (int i = 0; i < WorkerCount; i++)
                {
                    sampled += SampledPerThread[i];
                    overlap += OverlapPerThread[i];
                    jam += JamPerThread[i];
                }

                MetricsLookup[MetricsEntity] = new HordeTuningQuickMetrics
                {
                    Sampled = sampled,
                    OverlapHits = overlap,
                    JamHits = jam,
                    Dt = Dt
                };
            }
        }

        [BurstCompile]
        private struct ClearSampledMapJob : IJob
        {
            public NativeParallelHashMap<Entity, float2> Map;

            public void Execute()
            {
                Map.Clear();
            }
        }

        [BurstCompile]
        private struct StoreSampledPositionsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<float2> Positions;
            public int SampleStride;
            public NativeParallelHashMap<Entity, float2>.ParallelWriter Writer;

            public void Execute(int index)
            {
                if ((index % SampleStride) != 0)
                {
                    return;
                }

                Writer.TryAdd(Entities[index], Positions[index]);
            }
        }

        [BurstCompile]
        private static int HashCell(int x, int y)
        {
            return (x * 73856093) ^ (y * 19349663);
        }
    }
}
