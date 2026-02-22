using Project.Map;
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
        private const int SpeedHistogramBins = 32;
        private const float SpeedHistogramMax = 2f;
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
        private NativeArray<int> _hardJamPerThread;
        private NativeArray<int> _capReachedPerThread;
        private NativeArray<int> _processedNeighborsPerThread;
        private NativeArray<int> _speedSamplesPerThread;
        private NativeArray<int> _backpressureActivePerThread;
        private NativeArray<float> _speedSumPerThread;
        private NativeArray<float> _speedFractionSumPerThread;
        private NativeArray<float> _speedScaleSumPerThread;
        private NativeArray<float> _speedMinPerThread;
        private NativeArray<float> _speedMaxPerThread;
        private NativeArray<float> _speedScaleMinPerThread;
        private NativeArray<int> _speedHistogramPerThread;
        private NativeArray<int> _speedHistogramReduced;
        private NativeArray<float> _pressureSnapshot;
        private ComponentLookup<HordeTuningQuickMetrics> _metricsLookup;
        private BufferLookup<PressureCell> _pressureLookup;
        private EntityQuery _pressureBufferQuery;

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
            _hardJamPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _capReachedPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _processedNeighborsPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedSamplesPerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _backpressureActivePerThread = new NativeArray<int>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedSumPerThread = new NativeArray<float>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedFractionSumPerThread = new NativeArray<float>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedScaleSumPerThread = new NativeArray<float>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedMinPerThread = new NativeArray<float>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedMaxPerThread = new NativeArray<float>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedScaleMinPerThread = new NativeArray<float>(_workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedHistogramPerThread = new NativeArray<int>(_workerCount * SpeedHistogramBins, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _speedHistogramReduced = new NativeArray<int>(SpeedHistogramBins, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _metricsLookup = state.GetComponentLookup<HordeTuningQuickMetrics>(false);
            _pressureLookup = state.GetBufferLookup<PressureCell>(true);
            _pressureBufferQuery = state.GetEntityQuery(ComponentType.ReadOnly<PressureFieldBufferTag>());

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
            state.RequireForUpdate<FlowFieldSingleton>();
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

            if (_hardJamPerThread.IsCreated)
            {
                _hardJamPerThread.Dispose();
            }

            if (_capReachedPerThread.IsCreated)
            {
                _capReachedPerThread.Dispose();
            }

            if (_processedNeighborsPerThread.IsCreated)
            {
                _processedNeighborsPerThread.Dispose();
            }

            if (_speedSamplesPerThread.IsCreated)
            {
                _speedSamplesPerThread.Dispose();
            }

            if (_backpressureActivePerThread.IsCreated)
            {
                _backpressureActivePerThread.Dispose();
            }

            if (_speedSumPerThread.IsCreated)
            {
                _speedSumPerThread.Dispose();
            }

            if (_speedFractionSumPerThread.IsCreated)
            {
                _speedFractionSumPerThread.Dispose();
            }

            if (_speedScaleSumPerThread.IsCreated)
            {
                _speedScaleSumPerThread.Dispose();
            }

            if (_speedMinPerThread.IsCreated)
            {
                _speedMinPerThread.Dispose();
            }

            if (_speedMaxPerThread.IsCreated)
            {
                _speedMaxPerThread.Dispose();
            }

            if (_speedScaleMinPerThread.IsCreated)
            {
                _speedScaleMinPerThread.Dispose();
            }

            if (_speedHistogramPerThread.IsCreated)
            {
                _speedHistogramPerThread.Dispose();
            }

            if (_speedHistogramReduced.IsCreated)
            {
                _speedHistogramReduced.Dispose();
            }

            if (_pressureSnapshot.IsCreated)
            {
                _pressureSnapshot.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            EnsureThreadCounterCapacity(ref state);

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
                    $"pressureMaxPushPerFrame={pressureConfig.MaxPushPerFrame:F2} backpressureThreshold={pressureConfig.BackpressureThreshold:F2} " +
                    $"minSpeedFactor={pressureConfig.MinSpeedFactor:F2} maxSpeedFactor={pressureConfig.BackpressureMaxFactor:F2} backpressureK={pressureConfig.BackpressureK:F2} " +
                    $"sepMaxPushPerFrame={separationConfig.MaxPushPerFrame:F2} sepIterations={separationConfig.Iterations} radius={separationConfig.Radius:F3} " +
                    $"logEveryFrames={quickConfig.LogEveryNFrames} sampleStride={quickConfig.SampleStride}");
                s_loggedConfigOnce = true;
            }

            if (_pendingMetricsLog)
            {
                HordeTuningQuickMetrics metrics = SystemAPI.GetSingleton<HordeTuningQuickMetrics>();
                float logIntervalSeconds = math.max(0f, metrics.Dt);
                float simDt = deltaTime;
                float referenceMoveSpeed = 1f;
                float refMaxStep = referenceMoveSpeed * simDt;
                float pressureConfigBudget = math.max(0f, pressureConfig.MaxPushPerFrame) * simDt;
                float pressureSpeedBudget = refMaxStep * math.clamp(pressureConfig.SpeedFractionCap, 0f, 1f);
                float pressureEffectiveCap = math.min(pressureConfigBudget, pressureSpeedBudget);
                float separationCap = math.max(0f, separationConfig.MaxPushPerFrame) * simDt;

                float overlapPct = metrics.Sampled > 0 ? (100f * metrics.OverlapHits / metrics.Sampled) : 0f;
                float jamPct = metrics.Sampled > 0 ? (100f * metrics.JamHits / metrics.Sampled) : 0f;
                float hardJamPct = metrics.Sampled > 0 ? (100f * metrics.HardJamEnabledHits / metrics.Sampled) : 0f;
                float capReachedPct = metrics.Sampled > 0 ? (100f * metrics.CapReachedHits / metrics.Sampled) : 0f;
                float avgProcessedNeighbors = metrics.Sampled > 0 ? ((float)metrics.ProcessedNeighborsSum / metrics.Sampled) : 0f;
                float activeBackpressurePct = metrics.Sampled > 0 ? (100f * metrics.BackpressureActiveHits / metrics.Sampled) : 0f;

                UnityEngine.Debug.Log(
                    $"[HordeTune] logIntervalSeconds={logIntervalSeconds:F4} simDt={simDt:F4} sampled={metrics.Sampled} overlap={overlapPct:F1}% jam={jamPct:F1}% " +
                    $"speed(avg={metrics.AvgSpeed:F2} p50={metrics.P50Speed:F2} p90={metrics.P90Speed:F2} min={metrics.MinSpeed:F2} max={metrics.MaxSpeed:F2}) " +
                    $"frac(avg={metrics.AvgSpeedFraction:F2}) " +
                    $"backpressure(pressureThreshold={pressureConfig.BackpressureThreshold:F2} k={pressureConfig.BackpressureK:F2} active={activeBackpressurePct:F1}% avgScale={metrics.AvgSpeedScale:F2} minScale={metrics.MinSpeedScale:F2}) " +
                    $"capReachedHits={capReachedPct:F1}% avgProcessedNeighbors={avgProcessedNeighbors:F2} hardJamEnabled={hardJamPct:F1}% " +
                    $"sepCapFrame={separationCap:F4} pressureCapFrame={pressureEffectiveCap:F4} " +
                    $"sepMaxPushPerFrame={separationConfig.MaxPushPerFrame:F3} pressureMaxPushPerFrame={pressureConfig.MaxPushPerFrame:F3} pressureSpeedFractionCap={pressureConfig.SpeedFractionCap:F2} " +
                    $"backpressureThreshold={pressureConfig.BackpressureThreshold:F2} backpressureK={pressureConfig.BackpressureK:F2} " +
                    $"radius={math.max(0.001f, separationConfig.Radius):F3}");

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
            float dtWindowForSpeed = math.max(deltaTime, _elapsedSinceTick);
            _elapsedSinceTick = 0f;
            float backpressureThreshold = math.max(0f, pressureConfig.BackpressureThreshold);
            float backpressureK = math.max(0f, pressureConfig.BackpressureK);
            float minSpeedFactor = math.clamp(pressureConfig.MinSpeedFactor, 0f, 1f);
            float maxSpeedFactor = math.clamp(pressureConfig.BackpressureMaxFactor, 0f, 1f);
            if (maxSpeedFactor < minSpeedFactor)
            {
                maxSpeedFactor = minSpeedFactor;
            }

            FlowFieldSingleton flowSingleton = SystemAPI.GetSingleton<FlowFieldSingleton>();
            if (!flowSingleton.Blob.IsCreated)
            {
                return;
            }
            int flowCellCount = flowSingleton.Blob.Value.Width * flowSingleton.Blob.Value.Height;
            if (flowCellCount <= 0)
            {
                return;
            }

            EnsurePressureSnapshotCapacity(ref state, flowCellCount);
            _pressureLookup.Update(ref state);
            Entity pressureFieldEntity = _pressureBufferQuery.IsEmptyIgnoreFilter
                ? Entity.Null
                : _pressureBufferQuery.GetSingletonEntity();

            EnsureCapacity(count, sampleStride);
            _entities.ResizeUninitialized(count);
            _positions.ResizeUninitialized(count);
            _moveSpeeds.ResizeUninitialized(count);

            JobHandle dependency = state.Dependency;
            int threadLen = _sampledPerThread.Length;

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

            ClearScalarCountersJob clearScalarJob = new ClearScalarCountersJob
            {
                Sampled = _sampledPerThread,
                Overlap = _overlapPerThread,
                Jam = _jamPerThread,
                HardJam = _hardJamPerThread,
                CapReached = _capReachedPerThread,
                ProcessedNeighbors = _processedNeighborsPerThread,
                SpeedSamples = _speedSamplesPerThread,
                BackpressureActive = _backpressureActivePerThread,
                SpeedSum = _speedSumPerThread,
                SpeedFractionSum = _speedFractionSumPerThread,
                SpeedScaleSum = _speedScaleSumPerThread,
                SpeedMin = _speedMinPerThread,
                SpeedMax = _speedMaxPerThread,
                SpeedScaleMin = _speedScaleMinPerThread
            };
            dependency = clearScalarJob.Schedule(threadLen, 64, dependency);

            ClearHistogramJob clearHistogramJob = new ClearHistogramJob
            {
                SpeedHistogram = _speedHistogramPerThread
            };
            dependency = clearHistogramJob.Schedule(_speedHistogramPerThread.Length, 256, dependency);

            CopyPressureSnapshotJob copyPressureJob = new CopyPressureSnapshotJob
            {
                PressureLookup = _pressureLookup,
                PressureFieldEntity = pressureFieldEntity,
                PressureSnapshot = _pressureSnapshot,
                FlowCellCount = flowCellCount
            };
            dependency = copyPressureJob.Schedule(dependency);

            EvaluateQuickMetricsJob evaluateJob = new EvaluateQuickMetricsJob
            {
                Entities = _entities.AsArray(),
                Positions = _positions.AsArray(),
                MoveSpeeds = _moveSpeeds.AsArray(),
                Grid = _cellToIndex,
                Flow = flowSingleton.Blob,
                PressureSnapshot = _pressureSnapshot,
                PreviousSampledPositions = _previousSampledPositions,
                SampledPerThread = _sampledPerThread,
                OverlapPerThread = _overlapPerThread,
                JamPerThread = _jamPerThread,
                HardJamPerThread = _hardJamPerThread,
                CapReachedPerThread = _capReachedPerThread,
                ProcessedNeighborsPerThread = _processedNeighborsPerThread,
                SpeedSamplesPerThread = _speedSamplesPerThread,
                BackpressureActivePerThread = _backpressureActivePerThread,
                SpeedSumPerThread = _speedSumPerThread,
                SpeedFractionSumPerThread = _speedFractionSumPerThread,
                SpeedScaleSumPerThread = _speedScaleSumPerThread,
                SpeedMinPerThread = _speedMinPerThread,
                SpeedMaxPerThread = _speedMaxPerThread,
                SpeedScaleMinPerThread = _speedScaleMinPerThread,
                SpeedHistogramPerThread = _speedHistogramPerThread,
                InvCellSize = invCellSize,
                MinDistSq = minDistSq,
                InfluenceRadiusSq = influenceRadiusSq,
                MaxNeighbors = maxNeighbors,
                SampleStride = sampleStride,
                DeltaTimeWindow = dtWindowForSpeed,
                SpeedThresholdFactor = 0.2f,
                SpeedHistogramBins = SpeedHistogramBins,
                SpeedHistogramMax = SpeedHistogramMax,
                BackpressureThreshold = backpressureThreshold,
                BackpressureK = backpressureK,
                MinSpeedFactor = minSpeedFactor,
                MaxSpeedFactor = maxSpeedFactor,
                PressureEnabled = pressureConfig.Enabled,
                WorkerCount = threadLen
            };
            dependency = evaluateJob.Schedule(count, 128, dependency);

            _metricsLookup.Update(ref state);
            ReduceQuickMetricsJob reduceJob = new ReduceQuickMetricsJob
            {
                SampledPerThread = _sampledPerThread,
                OverlapPerThread = _overlapPerThread,
                JamPerThread = _jamPerThread,
                HardJamPerThread = _hardJamPerThread,
                CapReachedPerThread = _capReachedPerThread,
                ProcessedNeighborsPerThread = _processedNeighborsPerThread,
                SpeedSamplesPerThread = _speedSamplesPerThread,
                BackpressureActivePerThread = _backpressureActivePerThread,
                SpeedSumPerThread = _speedSumPerThread,
                SpeedFractionSumPerThread = _speedFractionSumPerThread,
                SpeedScaleSumPerThread = _speedScaleSumPerThread,
                SpeedMinPerThread = _speedMinPerThread,
                SpeedMaxPerThread = _speedMaxPerThread,
                SpeedScaleMinPerThread = _speedScaleMinPerThread,
                SpeedHistogramPerThread = _speedHistogramPerThread,
                ReducedHistogram = _speedHistogramReduced,
                MetricsLookup = _metricsLookup,
                MetricsEntity = _metricsEntity,
                Dt = dtWindowForSpeed,
                HistogramBins = SpeedHistogramBins,
                SpeedHistogramMax = SpeedHistogramMax,
                WorkerCount = threadLen
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

        private void EnsurePressureSnapshotCapacity(ref SystemState state, int flowCellCount)
        {
            int desired = math.max(1, flowCellCount);
            if (_pressureSnapshot.IsCreated && _pressureSnapshot.Length == desired)
            {
                return;
            }

            JobHandle disposeHandle = state.Dependency;
            disposeHandle = DisposeIfCreated(_pressureSnapshot, disposeHandle);
            state.Dependency = disposeHandle;
            _pressureSnapshot = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        private void EnsureThreadCounterCapacity(ref SystemState state)
        {
            int desired = math.max(1, JobsUtility.MaxJobThreadCount);
            bool recreateAllThreadArrays =
                desired != _workerCount ||
                !_sampledPerThread.IsCreated ||
                _sampledPerThread.Length != desired ||
                !_overlapPerThread.IsCreated ||
                _overlapPerThread.Length != desired ||
                !_jamPerThread.IsCreated ||
                _jamPerThread.Length != desired ||
                !_hardJamPerThread.IsCreated ||
                _hardJamPerThread.Length != desired ||
                !_capReachedPerThread.IsCreated ||
                _capReachedPerThread.Length != desired ||
                !_processedNeighborsPerThread.IsCreated ||
                _processedNeighborsPerThread.Length != desired ||
                !_speedSamplesPerThread.IsCreated ||
                _speedSamplesPerThread.Length != desired ||
                !_backpressureActivePerThread.IsCreated ||
                _backpressureActivePerThread.Length != desired ||
                !_speedSumPerThread.IsCreated ||
                _speedSumPerThread.Length != desired ||
                !_speedFractionSumPerThread.IsCreated ||
                _speedFractionSumPerThread.Length != desired ||
                !_speedScaleSumPerThread.IsCreated ||
                _speedScaleSumPerThread.Length != desired ||
                !_speedMinPerThread.IsCreated ||
                _speedMinPerThread.Length != desired ||
                !_speedMaxPerThread.IsCreated ||
                _speedMaxPerThread.Length != desired ||
                !_speedScaleMinPerThread.IsCreated ||
                _speedScaleMinPerThread.Length != desired ||
                !_speedHistogramPerThread.IsCreated ||
                _speedHistogramPerThread.Length != (desired * SpeedHistogramBins);

            if (recreateAllThreadArrays)
            {
                JobHandle disposeHandle = state.Dependency;
                disposeHandle = DisposeIfCreated(_sampledPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_overlapPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_jamPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_hardJamPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_capReachedPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_processedNeighborsPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedSamplesPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_backpressureActivePerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedSumPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedFractionSumPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedScaleSumPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedMinPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedMaxPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedScaleMinPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedHistogramPerThread, disposeHandle);
                disposeHandle = DisposeIfCreated(_speedHistogramReduced, disposeHandle);
                state.Dependency = disposeHandle;

                _sampledPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _overlapPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _jamPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _hardJamPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _capReachedPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _processedNeighborsPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedSamplesPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _backpressureActivePerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedSumPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedFractionSumPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedScaleSumPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedMinPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedMaxPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedScaleMinPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedHistogramPerThread = new NativeArray<int>(desired * SpeedHistogramBins, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _speedHistogramReduced = new NativeArray<int>(SpeedHistogramBins, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _workerCount = desired;
                return;
            }

            if (!_speedHistogramReduced.IsCreated || _speedHistogramReduced.Length != SpeedHistogramBins)
            {
                JobHandle disposeHandle = state.Dependency;
                disposeHandle = DisposeIfCreated(_speedHistogramReduced, disposeHandle);
                state.Dependency = disposeHandle;
                _speedHistogramReduced = new NativeArray<int>(SpeedHistogramBins, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
        }

        private static JobHandle DisposeIfCreated<T>(NativeArray<T> array, JobHandle dependency)
            where T : struct
        {
            if (!array.IsCreated)
            {
                return dependency;
            }

            return array.Dispose(dependency);
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
        private struct CopyPressureSnapshotJob : IJob
        {
            [ReadOnly] public BufferLookup<PressureCell> PressureLookup;
            public Entity PressureFieldEntity;
            [NativeDisableParallelForRestriction] public NativeArray<float> PressureSnapshot;
            public int FlowCellCount;

            public void Execute()
            {
                int count = math.min(FlowCellCount, PressureSnapshot.Length);
                for (int i = 0; i < count; i++)
                {
                    PressureSnapshot[i] = 0f;
                }

                if (PressureFieldEntity == Entity.Null || !PressureLookup.HasBuffer(PressureFieldEntity))
                {
                    return;
                }

                DynamicBuffer<PressureCell> pressureBuffer = PressureLookup[PressureFieldEntity];
                int copyCount = math.min(count, pressureBuffer.Length);
                for (int i = 0; i < copyCount; i++)
                {
                    PressureSnapshot[i] = pressureBuffer[i].Value;
                }
            }
        }

        [BurstCompile]
        private struct ClearScalarCountersJob : IJobParallelFor
        {
            public NativeArray<int> Sampled;
            public NativeArray<int> Overlap;
            public NativeArray<int> Jam;
            public NativeArray<int> HardJam;
            public NativeArray<int> CapReached;
            public NativeArray<int> ProcessedNeighbors;
            public NativeArray<int> SpeedSamples;
            public NativeArray<int> BackpressureActive;
            public NativeArray<float> SpeedSum;
            public NativeArray<float> SpeedFractionSum;
            public NativeArray<float> SpeedScaleSum;
            public NativeArray<float> SpeedMin;
            public NativeArray<float> SpeedMax;
            public NativeArray<float> SpeedScaleMin;

            public void Execute(int index)
            {
                Sampled[index] = 0;
                Overlap[index] = 0;
                Jam[index] = 0;
                HardJam[index] = 0;
                CapReached[index] = 0;
                ProcessedNeighbors[index] = 0;
                SpeedSamples[index] = 0;
                BackpressureActive[index] = 0;
                SpeedSum[index] = 0f;
                SpeedFractionSum[index] = 0f;
                SpeedScaleSum[index] = 0f;
                SpeedMin[index] = float.MaxValue;
                SpeedMax[index] = 0f;
                SpeedScaleMin[index] = 1f;
            }
        }

        [BurstCompile]
        private struct ClearHistogramJob : IJobParallelFor
        {
            public NativeArray<int> SpeedHistogram;

            public void Execute(int index)
            {
                SpeedHistogram[index] = 0;
            }
        }

        [BurstCompile]
        private struct EvaluateQuickMetricsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<float2> Positions;
            [ReadOnly] public NativeArray<float> MoveSpeeds;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            [ReadOnly] public NativeArray<float> PressureSnapshot;
            [ReadOnly] public NativeParallelHashMap<Entity, float2> PreviousSampledPositions;
            [NativeDisableParallelForRestriction] public NativeArray<int> SampledPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> OverlapPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> JamPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> HardJamPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> CapReachedPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> ProcessedNeighborsPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> SpeedSamplesPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> BackpressureActivePerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SpeedSumPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SpeedFractionSumPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SpeedScaleSumPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SpeedMinPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SpeedMaxPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SpeedScaleMinPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> SpeedHistogramPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;
            public float InvCellSize;
            public float MinDistSq;
            public float InfluenceRadiusSq;
            public int MaxNeighbors;
            public int SampleStride;
            public float DeltaTimeWindow;
            public float SpeedThresholdFactor;
            public int SpeedHistogramBins;
            public float SpeedHistogramMax;
            public float BackpressureThreshold;
            public float BackpressureK;
            public float MinSpeedFactor;
            public float MaxSpeedFactor;
            public byte PressureEnabled;
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
                if (reachedCap)
                {
                    CapReachedPerThread[workerIndex] = CapReachedPerThread[workerIndex] + 1;
                }
                ProcessedNeighborsPerThread[workerIndex] = ProcessedNeighborsPerThread[workerIndex] + processed;

                float localPressure = ResolveLocalPressure(pos);
                float speedScale = ComputeSpeedScaleFromPressure(localPressure);
                SpeedScaleSumPerThread[workerIndex] = SpeedScaleSumPerThread[workerIndex] + speedScale;
                SpeedScaleMinPerThread[workerIndex] = math.min(SpeedScaleMinPerThread[workerIndex], speedScale);
                if (speedScale < 0.99f)
                {
                    BackpressureActivePerThread[workerIndex] = BackpressureActivePerThread[workerIndex] + 1;
                }

                bool dense = PressureEnabled != 0 && localPressure > BackpressureThreshold;
                bool slow = false;
                Entity entity = Entities[index];
                if (PreviousSampledPositions.TryGetValue(entity, out float2 previousPos))
                {
                    float dist = math.distance(pos, previousPos);
                    float speed = dist / math.max(1e-5f, DeltaTimeWindow);
                    float speedFraction = speed / math.max(1e-5f, MoveSpeeds[index]);
                    slow = speed < (MoveSpeeds[index] * SpeedThresholdFactor);

                    SpeedSamplesPerThread[workerIndex] = SpeedSamplesPerThread[workerIndex] + 1;
                    SpeedSumPerThread[workerIndex] = SpeedSumPerThread[workerIndex] + speed;
                    SpeedFractionSumPerThread[workerIndex] = SpeedFractionSumPerThread[workerIndex] + speedFraction;
                    SpeedMinPerThread[workerIndex] = math.min(SpeedMinPerThread[workerIndex], speed);
                    SpeedMaxPerThread[workerIndex] = math.max(SpeedMaxPerThread[workerIndex], speed);

                    float clampedSpeed = math.clamp(speed, 0f, SpeedHistogramMax);
                    int speedBin = (int)math.floor((clampedSpeed / SpeedHistogramMax) * (SpeedHistogramBins - 1));
                    speedBin = math.clamp(speedBin, 0, SpeedHistogramBins - 1);
                    int histIndex = (workerIndex * SpeedHistogramBins) + speedBin;
                    SpeedHistogramPerThread[histIndex] = SpeedHistogramPerThread[histIndex] + 1;
                }

                if (slow && dense)
                {
                    JamPerThread[workerIndex] = JamPerThread[workerIndex] + 1;
                }

                bool hardJamEnabled = dense || (dense && slow);
                if (hardJamEnabled)
                {
                    HardJamPerThread[workerIndex] = HardJamPerThread[workerIndex] + 1;
                }
            }

            private float ResolveLocalPressure(float2 position)
            {
                if (!Flow.IsCreated)
                {
                    return 0f;
                }

                ref FlowFieldBlob flow = ref Flow.Value;
                int2 cell = WorldToFlowGrid(position, ref flow);
                if (!IsInFlowBounds(cell, ref flow))
                {
                    return 0f;
                }

                int flowIndex = cell.x + (cell.y * flow.Width);
                if (flowIndex < 0 || flowIndex >= PressureSnapshot.Length)
                {
                    return 0f;
                }

                return math.max(0f, PressureSnapshot[flowIndex]);
            }

            private float ComputeSpeedScaleFromPressure(float localPressure)
            {
                if (PressureEnabled == 0)
                {
                    return 1f;
                }

                float excess = math.max(0f, localPressure - BackpressureThreshold);
                float speedScaleRaw = 1f / (1f + (BackpressureK * excess));
                return math.clamp(speedScaleRaw, MinSpeedFactor, MaxSpeedFactor);
            }

            private static int2 WorldToFlowGrid(float2 world, ref FlowFieldBlob flow)
            {
                float2 local = (world - flow.OriginWorld) / flow.CellSize;
                return (int2)math.floor(local);
            }

            private static bool IsInFlowBounds(int2 grid, ref FlowFieldBlob flow)
            {
                return grid.x >= 0 && grid.y >= 0 && grid.x < flow.Width && grid.y < flow.Height;
            }
        }

        [BurstCompile]
        private struct ReduceQuickMetricsJob : IJob
        {
            [ReadOnly] public NativeArray<int> SampledPerThread;
            [ReadOnly] public NativeArray<int> OverlapPerThread;
            [ReadOnly] public NativeArray<int> JamPerThread;
            [ReadOnly] public NativeArray<int> HardJamPerThread;
            [ReadOnly] public NativeArray<int> CapReachedPerThread;
            [ReadOnly] public NativeArray<int> ProcessedNeighborsPerThread;
            [ReadOnly] public NativeArray<int> SpeedSamplesPerThread;
            [ReadOnly] public NativeArray<int> BackpressureActivePerThread;
            [ReadOnly] public NativeArray<float> SpeedSumPerThread;
            [ReadOnly] public NativeArray<float> SpeedFractionSumPerThread;
            [ReadOnly] public NativeArray<float> SpeedScaleSumPerThread;
            [ReadOnly] public NativeArray<float> SpeedMinPerThread;
            [ReadOnly] public NativeArray<float> SpeedMaxPerThread;
            [ReadOnly] public NativeArray<float> SpeedScaleMinPerThread;
            [ReadOnly] public NativeArray<int> SpeedHistogramPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> ReducedHistogram;
            [NativeDisableParallelForRestriction] public ComponentLookup<HordeTuningQuickMetrics> MetricsLookup;
            public Entity MetricsEntity;
            public float Dt;
            public int HistogramBins;
            public float SpeedHistogramMax;
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
                int hardJam = 0;
                int capReached = 0;
                int processedNeighbors = 0;
                int speedSamples = 0;
                int backpressureActive = 0;
                float speedSum = 0f;
                float speedFractionSum = 0f;
                float speedScaleSum = 0f;
                float minSpeed = float.MaxValue;
                float maxSpeed = 0f;
                float minSpeedScale = 1f;

                int totalHistogramLength = WorkerCount * HistogramBins;
                for (int i = 0; i < HistogramBins; i++)
                {
                    ReducedHistogram[i] = 0;
                }

                for (int i = 0; i < WorkerCount; i++)
                {
                    sampled += SampledPerThread[i];
                    overlap += OverlapPerThread[i];
                    jam += JamPerThread[i];
                    hardJam += HardJamPerThread[i];
                    capReached += CapReachedPerThread[i];
                    processedNeighbors += ProcessedNeighborsPerThread[i];
                    speedSamples += SpeedSamplesPerThread[i];
                    backpressureActive += BackpressureActivePerThread[i];
                    speedSum += SpeedSumPerThread[i];
                    speedFractionSum += SpeedFractionSumPerThread[i];
                    speedScaleSum += SpeedScaleSumPerThread[i];
                    minSpeed = math.min(minSpeed, SpeedMinPerThread[i]);
                    maxSpeed = math.max(maxSpeed, SpeedMaxPerThread[i]);
                    minSpeedScale = math.min(minSpeedScale, SpeedScaleMinPerThread[i]);
                }

                for (int i = 0; i < totalHistogramLength; i++)
                {
                    int bin = i % HistogramBins;
                    ReducedHistogram[bin] = ReducedHistogram[bin] + SpeedHistogramPerThread[i];
                }

                float p50Speed = ResolvePercentile(ReducedHistogram, speedSamples, 0.50f);
                float p90Speed = ResolvePercentile(ReducedHistogram, speedSamples, 0.90f);
                float avgSpeed = speedSamples > 0 ? (speedSum / speedSamples) : 0f;
                float avgSpeedFraction = speedSamples > 0 ? (speedFractionSum / speedSamples) : 0f;
                float avgSpeedScale = sampled > 0 ? (speedScaleSum / sampled) : 1f;
                if (speedSamples <= 0)
                {
                    minSpeed = 0f;
                }
                if (sampled <= 0)
                {
                    minSpeedScale = 1f;
                }

                MetricsLookup[MetricsEntity] = new HordeTuningQuickMetrics
                {
                    Sampled = sampled,
                    OverlapHits = overlap,
                    JamHits = jam,
                    HardJamEnabledHits = hardJam,
                    CapReachedHits = capReached,
                    ProcessedNeighborsSum = processedNeighbors,
                    SpeedSamples = speedSamples,
                    BackpressureActiveHits = backpressureActive,
                    Dt = Dt,
                    AvgSpeed = avgSpeed,
                    P50Speed = p50Speed,
                    P90Speed = p90Speed,
                    MinSpeed = minSpeed,
                    MaxSpeed = maxSpeed,
                    AvgSpeedFraction = avgSpeedFraction,
                    AvgSpeedScale = avgSpeedScale,
                    MinSpeedScale = minSpeedScale
                };
            }

            private float ResolvePercentile(NativeArray<int> histogram, int sampleCount, float percentile)
            {
                if (sampleCount <= 0 || HistogramBins <= 0)
                {
                    return 0f;
                }

                int target = math.max(1, (int)math.ceil(sampleCount * percentile));
                int cumulative = 0;
                for (int i = 0; i < HistogramBins; i++)
                {
                    cumulative += histogram[i];
                    if (cumulative >= target)
                    {
                        float t = HistogramBins > 1 ? ((float)i / (HistogramBins - 1)) : 0f;
                        return t * SpeedHistogramMax;
                    }
                }

                return SpeedHistogramMax;
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
