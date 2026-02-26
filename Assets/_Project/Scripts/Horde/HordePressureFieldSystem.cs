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
    [UpdateAfter(typeof(ZombieSteeringSystem))]
    [UpdateAfter(typeof(HordeBackpressureSystem))]
    [UpdateBefore(typeof(HordeSeparationSystem))]
    [UpdateBefore(typeof(HordeHardSeparationSystem))]
    public partial struct HordePressureFieldSystem : ISystem
    {
        private const float Epsilon = 1e-6f;
        private const float Diagonal = 0.70710677f;
        private const int DebugLogIntervalFrames = 120;
        private const int DebugCounterCount = 6;
        private const int DebugCounterEligible = 0;
        private const int DebugCounterPressureApplied = 1;
        private const int DebugCounterTangentApplied = 2;
        private const int DebugCounterDensityValid = 3;
        private const int DebugCounterInvalidWallSample = 4;
        private const int DebugCounterFinalDeltaApplied = 5;
        private const int DebugSumCount = 3;
        private const int DebugSumTangentMag = 0;
        private const int DebugSumPressureMag = 1;
        private const int DebugSumFinalDeltaMag = 2;

        private NativeArray<int> _density;
        private NativeArray<int> _densityPerThread;
        private int _workerCount;
        private NativeArray<float> _pressureA;
        private NativeArray<float> _pressureB;
        private NativeArray<int> _debugCountersPerThread;
        private NativeArray<float> _debugSumsPerThread;
        private int _cellCount;
        private int _frameIndex;
        private byte _activePressureBuffer;
        private Entity _pressureFieldEntity;
        private EntityQuery _pressureBufferQuery;
        private BufferLookup<PressureCell> _pressureLookup;
        private JobHandle _lastApplyPressureHandle;
        private byte _hasLastApplyPressureHandle;
        private int _lastWallTangentDebugLogFrame;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static bool s_loggedMissingPressureConfigCreatedOnce;
#endif

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ZombieTag>();
            state.RequireForUpdate<ZombieMoveSpeed>();
            state.RequireForUpdate<FlowFieldSingleton>();
            state.RequireForUpdate<MapRuntimeData>();

            EnsurePressureConfigSingleton(ref state);

            _pressureBufferQuery = state.GetEntityQuery(ComponentType.ReadWrite<PressureFieldBufferTag>());
            if (_pressureBufferQuery.IsEmptyIgnoreFilter)
            {
                _pressureFieldEntity = state.EntityManager.CreateEntity(typeof(PressureFieldBufferTag));
            }
            else
            {
                _pressureFieldEntity = _pressureBufferQuery.GetSingletonEntity();
            }

            if (!state.EntityManager.HasBuffer<PressureCell>(_pressureFieldEntity))
            {
                state.EntityManager.AddBuffer<PressureCell>(_pressureFieldEntity);
            }

            _pressureLookup = state.GetBufferLookup<PressureCell>(false);
            int maxWorkerCount = math.max(1, JobsUtility.MaxJobThreadCount);
            _debugCountersPerThread = new NativeArray<int>(maxWorkerCount * DebugCounterCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _debugSumsPerThread = new NativeArray<float>(maxWorkerCount * DebugSumCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _lastApplyPressureHandle = default;
            _hasLastApplyPressureHandle = 0;
            _lastWallTangentDebugLogFrame = -DebugLogIntervalFrames;
            state.RequireForUpdate<HordePressureConfig>();
        }

        private static void EnsurePressureConfigSingleton(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;
            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordePressureConfig>());
            int configCount = configQuery.CalculateEntityCount();

            if (configCount <= 0)
            {
                Entity configEntity = entityManager.CreateEntity(typeof(HordePressureConfig));
                entityManager.SetComponentData(configEntity, CreateDefaultPressureConfig());
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!s_loggedMissingPressureConfigCreatedOnce)
                {
                    UnityEngine.Debug.Log("[HordePressure] Created missing HordePressureConfig singleton using defaults.");
                    s_loggedMissingPressureConfigCreatedOnce = true;
                }
#endif
                return;
            }

            if (configCount > 1)
            {
                using NativeArray<Entity> entities = configQuery.ToEntityArray(Allocator.Temp);
                Entity keep = entities[0];
                for (int i = 1; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (entityManager.Exists(entity))
                    {
                        entityManager.DestroyEntity(entity);
                    }
                }

                if (keep != Entity.Null && entityManager.Exists(keep) && !entityManager.HasComponent<HordePressureConfig>(keep))
                {
                    entityManager.AddComponentData(keep, CreateDefaultPressureConfig());
                }
            }

        }

        private static HordePressureConfig CreateDefaultPressureConfig()
        {
            return new HordePressureConfig
            {
                Enabled = 1,
                TargetUnitsPerCell = 1.2f,

                PressureStrength = 13.0f,
                MaxPushPerFrame = 12.0f,
                SpeedFractionCap = 22.0f,

                PressureParallelScale = 22f,
                PressurePerpScale = 22.0f,

                WallTangentStrength = 6.0f,
                WallTangentMaxPushPerFrame = 6.0f,
                WallNearDistanceCells = 6.0f,
                DenseUnitsPerCellThreshold = 0.5f,

                // Backpressure: keep flow from endlessly feeding the queue
                BackpressureThreshold = 1.0f,
                MinSpeedFactor = 0.2f,
                BackpressureK = 0.35f,
                BackpressureMaxFactor = 12222.0f,

                BlockedCellPenalty = 6.0f,
                FieldUpdateIntervalFrames = 1,
                BlurPasses = 1,

                DisablePairwiseSeparationWhenPressureEnabled = 0,

                // Turn on temporarily to confirm gating triggers
                EnableWallTangentDriftDebug = 1,

                DebugForceTangent = 1
            };
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_density.IsCreated)
            {
                _density.Dispose();
            }

            if (_densityPerThread.IsCreated)
            {
                _densityPerThread.Dispose();
            }

            if (_pressureA.IsCreated)
            {
                _pressureA.Dispose();
            }

            if (_pressureB.IsCreated)
            {
                _pressureB.Dispose();
            }

            if (_debugCountersPerThread.IsCreated)
            {
                _debugCountersPerThread.Dispose();
            }

            if (_debugSumsPerThread.IsCreated)
            {
                _debugSumsPerThread.Dispose();
            }

            _cellCount = 0;
            _workerCount = 0;
            _frameIndex = 0;
            _activePressureBuffer = 0;
            _pressureFieldEntity = Entity.Null;
            _lastApplyPressureHandle = default;
            _hasLastApplyPressureHandle = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            FlowFieldSingleton flowSingleton = SystemAPI.GetSingleton<FlowFieldSingleton>();
            if (!flowSingleton.Blob.IsCreated)
            {
                return;
            }

            MapRuntimeData mapData = SystemAPI.GetSingleton<MapRuntimeData>();

            HordePressureConfig config = SystemAPI.GetSingleton<HordePressureConfig>();
            bool changedDefaults = false;
            if (config.BackpressureThreshold < 0f)
            {
                config.BackpressureThreshold = 7.0f;
                changedDefaults = true;
            }

            if (config.BackpressureK < 0f)
            {
                config.BackpressureK = 0.20f;
                changedDefaults = true;
            }

            if (config.MinSpeedFactor < 0f)
            {
                config.MinSpeedFactor = 0.30f;
                changedDefaults = true;
            }

            if (config.BackpressureMaxFactor < 0f)
            {
                config.BackpressureMaxFactor = 1.0f;
                changedDefaults = true;
            }

            if (config.PressureParallelScale < 0f)
            {
                config.PressureParallelScale = 0.35f;
                changedDefaults = true;
            }

            if (config.PressurePerpScale < 0f)
            {
                config.PressurePerpScale = 1.25f;
                changedDefaults = true;
            }

            if (config.WallTangentStrength < 0f)
            {
                config.WallTangentStrength = 0.75f;
                changedDefaults = true;
            }

            if (config.WallTangentMaxPushPerFrame < 0f)
            {
                config.WallTangentMaxPushPerFrame = 1.25f;
                changedDefaults = true;
            }

            if (config.WallNearDistanceCells < 0f)
            {
                config.WallNearDistanceCells = 1.25f;
                changedDefaults = true;
            }

            if (config.DenseUnitsPerCellThreshold < 0f)
            {
                config.DenseUnitsPerCellThreshold = 5.0f;
                changedDefaults = true;
            }

            if (changedDefaults)
            {
                SystemAPI.SetSingleton(config);
            }

            if (config.Enabled == 0)
            {
                _frameIndex++;
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            TryLogWallTangentDebug(config);
#endif

            bool hasWallField = SystemAPI.TryGetSingleton(out WallFieldSingleton wallSingleton) && wallSingleton.Blob.IsCreated;
            BlobAssetReference<WallFieldBlob> wallBlob = hasWallField ? wallSingleton.Blob : default;

            ref FlowFieldBlob flow = ref flowSingleton.Blob.Value;
            int cellCount = flow.Width * flow.Height;
            if (cellCount <= 0)
            {
                _frameIndex++;
                return;
            }

            bool resized = EnsureFieldSize(cellCount);
            if (!_density.IsCreated || !_densityPerThread.IsCreated || !_pressureA.IsCreated || !_pressureB.IsCreated)
            {
                _frameIndex++;
                return;
            }

            float targetUnitsPerCell = math.max(0f, config.TargetUnitsPerCell);
            float pressureStrength = math.max(0f, config.PressureStrength);
            float speedFractionCap = math.clamp(config.SpeedFractionCap, 0f, 1f);
            float maxPushFromConfig = math.max(0f, config.MaxPushPerFrame) * deltaTime;
            float referenceMoveSpeed = 1f;
            float maxPushFromSpeed = referenceMoveSpeed * deltaTime * speedFractionCap;
            float maxPushThisFrame = math.min(maxPushFromConfig, maxPushFromSpeed);
            float blockedPenalty = math.max(0f, config.BlockedCellPenalty);
            float pressureParallelScale = math.max(0f, config.PressureParallelScale);
            float pressurePerpScale = math.max(0f, config.PressurePerpScale);
            float wallTangentStrength = math.max(0f, config.WallTangentStrength);
            float wallTangentMaxPush = math.max(0f, config.WallTangentMaxPushPerFrame) * deltaTime;
            float wallNearDistanceCells = math.max(0f, config.WallNearDistanceCells);
            float denseUnitsThreshold = math.max(0f, config.DenseUnitsPerCellThreshold);
            int fieldInterval = math.clamp(config.FieldUpdateIntervalFrames, 1, 8);
            int blurPasses = math.clamp(config.BlurPasses, 0, 2);
            bool shouldRebuild = resized || ((_frameIndex % fieldInterval) == 0);
            _pressureLookup.Update(ref state);

            JobHandle dependency = state.Dependency;
            if (_hasLastApplyPressureHandle != 0)
            {
                dependency = JobHandle.CombineDependencies(dependency, _lastApplyPressureHandle);
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            bool wallTangentDebugEnabled = config.EnableWallTangentDriftDebug != 0;
#else
            bool wallTangentDebugEnabled = false;
#endif
            bool debugForceTangentEnabled = wallTangentDebugEnabled && config.DebugForceTangent != 0;
            if (wallTangentDebugEnabled && _debugCountersPerThread.IsCreated)
            {
                ClearIntArrayJob clearWallTangentDebugJob = new ClearIntArrayJob
                {
                    Values = _debugCountersPerThread
                };
                dependency = clearWallTangentDebugJob.Schedule(_debugCountersPerThread.Length, 32, dependency);
            }
            if (wallTangentDebugEnabled && _debugSumsPerThread.IsCreated)
            {
                ClearFloatArrayJob clearWallTangentDebugSumsJob = new ClearFloatArrayJob
                {
                    Values = _debugSumsPerThread
                };
                dependency = clearWallTangentDebugSumsJob.Schedule(_debugSumsPerThread.Length, 32, dependency);
            }
            NativeArray<float> activePressure = _activePressureBuffer == 0 ? _pressureA : _pressureB;

            if (_pressureFieldEntity == Entity.Null || !state.EntityManager.Exists(_pressureFieldEntity))
            {
                if (_pressureBufferQuery.IsEmptyIgnoreFilter)
                {
                    _pressureFieldEntity = state.EntityManager.CreateEntity(typeof(PressureFieldBufferTag));
                }
                else
                {
                    _pressureFieldEntity = _pressureBufferQuery.GetSingletonEntity();
                }

                if (!state.EntityManager.HasBuffer<PressureCell>(_pressureFieldEntity))
                {
                    state.EntityManager.AddBuffer<PressureCell>(_pressureFieldEntity);
                }

            }

            if (shouldRebuild)
            {
                ClearIntArrayJob clearDensityPerThreadJob = new ClearIntArrayJob
                {
                    Values = _densityPerThread
                };
                dependency = clearDensityPerThreadJob.Schedule(_densityPerThread.Length, 256, dependency);

                AccumulateDensityJob accumulateDensityJob = new AccumulateDensityJob
                {
                    DensityPerThread = _densityPerThread,
                    CellCount = cellCount,
                    WorkerCount = _workerCount,
                    Flow = flowSingleton.Blob
                };
                dependency = accumulateDensityJob.ScheduleParallel(dependency);

                ReduceDensityJob reduceDensityJob = new ReduceDensityJob
                {
                    DensityPerThread = _densityPerThread,
                    Density = _density,
                    CellCount = cellCount,
                    WorkerCount = _workerCount
                };
                dependency = reduceDensityJob.Schedule(cellCount, 256, dependency);

                BuildPressureJob buildPressureJob = new BuildPressureJob
                {
                    Density = _density,
                    Pressure = _pressureA,
                    Flow = flowSingleton.Blob,
                    TargetUnitsPerCell = targetUnitsPerCell,
                    BlockedPenalty = blockedPenalty
                };
                dependency = buildPressureJob.Schedule(cellCount, 256, dependency);

                bool readIsA = true;
                for (int pass = 0; pass < blurPasses; pass++)
                {
                    BlurPressureJob blurPressureJob = new BlurPressureJob
                    {
                        Input = readIsA ? _pressureA : _pressureB,
                        Output = readIsA ? _pressureB : _pressureA,
                        Flow = flowSingleton.Blob,
                        BlockedPenalty = blockedPenalty
                    };
                    dependency = blurPressureJob.Schedule(cellCount, 256, dependency);
                    readIsA = !readIsA;
                }

                _activePressureBuffer = readIsA ? (byte)0 : (byte)1;
                activePressure = readIsA ? _pressureA : _pressureB;
            }

            if (_pressureFieldEntity != Entity.Null && state.EntityManager.Exists(_pressureFieldEntity) && shouldRebuild)
            {
                DynamicBuffer<PressureCell> pressureBuffer = state.EntityManager.GetBuffer<PressureCell>(_pressureFieldEntity);
                if (pressureBuffer.Length != cellCount)
                {
                    pressureBuffer.ResizeUninitialized(cellCount);
                }

                PublishPressureToBufferJob publishPressureJob = new PublishPressureToBufferJob
                {
                    Source = activePressure,
                    PressureLookup = _pressureLookup,
                    PressureEntity = _pressureFieldEntity
                };
                dependency = publishPressureJob.Schedule(dependency);
            }

            ApplyPressureJob applyPressureJob = new ApplyPressureJob
            {
                Flow = flowSingleton.Blob,
                Wall = wallBlob,
                HasWallField = hasWallField ? (byte)1 : (byte)0,
                Pressure = activePressure,
                Density = _density,
                PressureStrength = pressureStrength,
                MaxPush = maxPushThisFrame,
                SpeedFractionCap = speedFractionCap,
                PressureParallelScale = pressureParallelScale,
                PressurePerpScale = pressurePerpScale,
                WallTangentStrength = wallTangentStrength,
                WallTangentMaxPush = wallTangentMaxPush,
                WallNearDistanceCells = wallNearDistanceCells,
                DenseUnitsPerCellThreshold = denseUnitsThreshold,
                BlockedPenalty = blockedPenalty,
                CenterWorld = mapData.CenterWorld,
                DeltaTime = deltaTime,
                WallTangentDebugEnabled = wallTangentDebugEnabled ? (byte)1 : (byte)0,
                DebugForceTangentEnabled = debugForceTangentEnabled ? (byte)1 : (byte)0,
                DebugCountersPerThread = _debugCountersPerThread,
                DebugSumsPerThread = _debugSumsPerThread
            };
            JobHandle applyHandle = applyPressureJob.ScheduleParallel(dependency);
            state.Dependency = applyHandle;
            _lastApplyPressureHandle = applyHandle;
            _hasLastApplyPressureHandle = 1;
            _frameIndex++;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void TryLogWallTangentDebug(HordePressureConfig config)
        {
            if (config.EnableWallTangentDriftDebug == 0)
            {
                return;
            }

            if ((_frameIndex - _lastWallTangentDebugLogFrame) < DebugLogIntervalFrames)
            {
                return;
            }

            if (_hasLastApplyPressureHandle == 0 || !_lastApplyPressureHandle.IsCompleted)
            {
                return;
            }

            _lastApplyPressureHandle.Complete();
            int eligible = SumDebugCounter(DebugCounterEligible);
            int pressureApplied = SumDebugCounter(DebugCounterPressureApplied);
            int tangentApplied = SumDebugCounter(DebugCounterTangentApplied);
            int densityValid = SumDebugCounter(DebugCounterDensityValid);
            int invalidWall = SumDebugCounter(DebugCounterInvalidWallSample);
            int finalDeltaApplied = SumDebugCounter(DebugCounterFinalDeltaApplied);
            float sumTan = SumDebugSum(DebugSumTangentMag);
            float sumPress = SumDebugSum(DebugSumPressureMag);
            float sumFinal = SumDebugSum(DebugSumFinalDeltaMag);
            float avgTan = tangentApplied > 0 ? (sumTan / tangentApplied) : 0f;
            float avgPress = pressureApplied > 0 ? (sumPress / pressureApplied) : 0f;
            float avgDelta = finalDeltaApplied > 0 ? (sumFinal / finalDeltaApplied) : 0f;

            _lastWallTangentDebugLogFrame = _frameIndex;
            UnityEngine.Debug.Log(
                "[HordePressure] eligible=" + eligible +
                " pressureApplied=" + pressureApplied +
                " tangentApplied=" + tangentApplied +
                " finalApplied=" + finalDeltaApplied +
                " avgTan=" + avgTan.ToString("F4") +
                " avgPress=" + avgPress.ToString("F4") +
                " avgDelta=" + avgDelta.ToString("F4") +
                " densityValid=" + densityValid +
                " invalidWall=" + invalidWall +
                " frame=" + _frameIndex + ".");
        }

        private int SumDebugCounter(int counterIndex)
        {
            if (!_debugCountersPerThread.IsCreated || counterIndex < 0 || counterIndex >= DebugCounterCount)
            {
                return 0;
            }

            int workerCount = _debugCountersPerThread.Length / DebugCounterCount;
            int sum = 0;
            for (int i = 0; i < workerCount; i++)
            {
                int baseIndex = i * DebugCounterCount;
                sum += _debugCountersPerThread[baseIndex + counterIndex];
            }

            return sum;
        }

        private float SumDebugSum(int sumIndex)
        {
            if (!_debugSumsPerThread.IsCreated || sumIndex < 0 || sumIndex >= DebugSumCount)
            {
                return 0f;
            }

            int workerCount = _debugSumsPerThread.Length / DebugSumCount;
            float sum = 0f;
            for (int i = 0; i < workerCount; i++)
            {
                int baseIndex = i * DebugSumCount;
                sum += _debugSumsPerThread[baseIndex + sumIndex];
            }

            return sum;
        }
#endif


        private bool EnsureFieldSize(int cellCount)
        {
            if (_cellCount == cellCount && _workerCount > 0 && _density.IsCreated && _densityPerThread.IsCreated && _pressureA.IsCreated && _pressureB.IsCreated)
            {
                return false;
            }

            if (_density.IsCreated)
            {
                _density.Dispose();
            }

            if (_densityPerThread.IsCreated)
            {
                _densityPerThread.Dispose();
            }

            if (_pressureA.IsCreated)
            {
                _pressureA.Dispose();
            }

            if (_pressureB.IsCreated)
            {
                _pressureB.Dispose();
            }

            _density = new NativeArray<int>(cellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _workerCount = math.max(1, JobsUtility.MaxJobThreadCount);
            _densityPerThread = new NativeArray<int>(cellCount * _workerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _pressureA = new NativeArray<float>(cellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _pressureB = new NativeArray<float>(cellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _cellCount = cellCount;
            _activePressureBuffer = 0;
            return true;
        }

        [BurstCompile]
        private struct ClearIntArrayJob : IJobParallelFor
        {
            public NativeArray<int> Values;

            public void Execute(int index)
            {
                Values[index] = 0;
            }
        }

        [BurstCompile]
        private struct ClearFloatArrayJob : IJobParallelFor
        {
            public NativeArray<float> Values;

            public void Execute(int index)
            {
                Values[index] = 0f;
            }
        }

        [BurstCompile]
        private partial struct AccumulateDensityJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public NativeArray<int> DensityPerThread;
            public int CellCount;
            public int WorkerCount;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            [NativeSetThreadIndex] public int ThreadIndex;

            private void Execute(in LocalTransform transform, in ZombieTag tag)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                int2 cell = WorldToFlowGrid(transform.Position.xy, ref flow);
                if (!IsInFlowBounds(cell, ref flow))
                {
                    return;
                }

                int cellIndex = cell.x + (cell.y * flow.Width);
                if (cellIndex < 0 || cellIndex >= CellCount)
                {
                    return;
                }

                if (flow.Dist[cellIndex] == ushort.MaxValue)
                {
                    return;
                }

                int workerIndex = math.clamp(ThreadIndex - 1, 0, WorkerCount - 1);
                int localIndex = (workerIndex * CellCount) + cellIndex;
                DensityPerThread[localIndex] = DensityPerThread[localIndex] + 1;
            }
        }

        [BurstCompile]
        private struct ReduceDensityJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> DensityPerThread;
            public NativeArray<int> Density;
            public int CellCount;
            public int WorkerCount;

            public void Execute(int cellIndex)
            {
                int sum = 0;
                for (int worker = 0; worker < WorkerCount; worker++)
                {
                    sum += DensityPerThread[(worker * CellCount) + cellIndex];
                }

                Density[cellIndex] = sum;
            }
        }

        [BurstCompile]
        private struct PublishPressureToBufferJob : IJob
        {
            [ReadOnly] public NativeArray<float> Source;
            public BufferLookup<PressureCell> PressureLookup;
            public Entity PressureEntity;

            public void Execute()
            {
                if (!PressureLookup.HasBuffer(PressureEntity))
                {
                    return;
                }

                DynamicBuffer<PressureCell> buffer = PressureLookup[PressureEntity];
                int count = math.min(Source.Length, buffer.Length);
                for (int i = 0; i < count; i++)
                {
                    buffer[i] = new PressureCell { Value = Source[i] };
                }
            }
        }

        [BurstCompile]
        private struct BuildPressureJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Density;
            public NativeArray<float> Pressure;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            public float TargetUnitsPerCell;
            public float BlockedPenalty;

            public void Execute(int index)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                if (index < 0 || index >= flow.Dist.Length || index >= Pressure.Length)
                {
                    return;
                }

                if (flow.Dist[index] == ushort.MaxValue)
                {
                    Pressure[index] = BlockedPenalty;
                    return;
                }

                float densityOver = Density[index] - TargetUnitsPerCell;
                Pressure[index] = math.max(0f, densityOver);
            }
        }

        [BurstCompile]
        private struct BlurPressureJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Input;
            public NativeArray<float> Output;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            public float BlockedPenalty;

            public void Execute(int index)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                if (index < 0 || index >= flow.Dist.Length || index >= Output.Length)
                {
                    return;
                }

                if (flow.Dist[index] == ushort.MaxValue)
                {
                    Output[index] = BlockedPenalty;
                    return;
                }

                int x = index % flow.Width;
                int y = index / flow.Width;
                float sum = 0f;
                int samples = 0;

                for (int oy = -1; oy <= 1; oy++)
                {
                    int ny = y + oy;
                    if (ny < 0 || ny >= flow.Height)
                    {
                        continue;
                    }

                    int row = ny * flow.Width;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox;
                        if (nx < 0 || nx >= flow.Width)
                        {
                            continue;
                        }

                        int nIndex = row + nx;
                        if (flow.Dist[nIndex] == ushort.MaxValue)
                        {
                            sum += BlockedPenalty;
                        }
                        else
                        {
                            sum += Input[nIndex];
                        }

                        samples++;
                    }
                }

                Output[index] = samples > 0 ? (sum / samples) : 0f;
            }
        }

        [BurstCompile]
        private partial struct ApplyPressureJob : IJobEntity
        {
            private const int DebugCounterCount = 6;
            private const int DebugCounterEligible = 0;
            private const int DebugCounterPressureApplied = 1;
            private const int DebugCounterTangentApplied = 2;
            private const int DebugCounterDensityValid = 3;
            private const int DebugCounterInvalidWallSample = 4;
            private const int DebugCounterFinalDeltaApplied = 5;
            private const int DebugSumCount = 3;
            private const int DebugSumTangentMag = 0;
            private const int DebugSumPressureMag = 1;
            private const int DebugSumFinalDeltaMag = 2;
            private const int DebugForceTangentFirstN = 32;
            private const float DebugForceTangentDelta = 0.05f;
            private const float TangentSignAlignDotEpsilon = 0.1f;

            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            [ReadOnly] public BlobAssetReference<WallFieldBlob> Wall;
            public byte HasWallField;
            [ReadOnly] public NativeArray<float> Pressure;
            [ReadOnly] public NativeArray<int> Density;
            public float PressureStrength;
            public float MaxPush;
            public float SpeedFractionCap;
            public float PressureParallelScale;
            public float PressurePerpScale;
            public float WallTangentStrength;
            public float WallTangentMaxPush;
            public float WallNearDistanceCells;
            public float DenseUnitsPerCellThreshold;
            public float BlockedPenalty;
            public float2 CenterWorld;
            public float DeltaTime;
            public byte WallTangentDebugEnabled;
            public byte DebugForceTangentEnabled;
            [NativeDisableParallelForRestriction] public NativeArray<int> DebugCountersPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> DebugSumsPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;

            private void Execute(
                [EntityIndexInQuery] int entityIndex,
                ref LocalTransform transform,
                in ZombieTag tag,
                in ZombieMoveSpeed moveSpeed,
                in ZombieGoalIntent goalIntent,
                in ZombieVelocity velocity)
            {
                if (DeltaTime <= 0f)
                {
                    return;
                }

                ref FlowFieldBlob flow = ref Flow.Value;
                float2 position = transform.Position.xy;
                int2 cell = WorldToFlowGrid(position, ref flow);
                if (!IsInFlowBounds(cell, ref flow))
                {
                    return;
                }

                int index = cell.x + (cell.y * flow.Width);
                if (index < 0 || index >= Pressure.Length || index >= flow.Dist.Length)
                {
                    return;
                }

                if (flow.Dist[index] == ushort.MaxValue)
                {
                    return;
                }

                bool debugForceTangent = WallTangentDebugEnabled != 0 && DebugForceTangentEnabled != 0 && entityIndex < DebugForceTangentFirstN;
                float localPressure = Pressure[index];
                if (localPressure <= Epsilon && !debugForceTangent)
                {
                    return;
                }

                float moveStep = math.max(0f, moveSpeed.Value) * DeltaTime;
                if (moveStep <= 0f)
                {
                    return;
                }

                float2 flowDirection = ResolveFlowDirection(index, position, ref flow);
                float flowDirLenSq = math.lengthsq(flowDirection);
                if (flowDirLenSq > Epsilon)
                {
                    flowDirection *= math.rsqrt(flowDirLenSq);
                }
                else
                {
                    flowDirection = float2.zero;
                }

                float2 desiredDir = ResolveDesiredDirection(goalIntent.Direction, velocity.Value, flowDirection);

                float speedBudget = moveStep * SpeedFractionCap;
                float effectiveCap = math.min(MaxPush, speedBudget);
                if (effectiveCap <= 0f)
                {
                    return;
                }

                CountDebugCounter(DebugCounterDensityValid);
                float localDensity = (index >= 0 && index < Density.Length) ? Density[index] : 0f;

                float2 pressureDelta = float2.zero;
                float2 direction = ResolvePressureDirection(cell, position, entityIndex, localPressure, ref flow);
                float dirLenSq = math.lengthsq(direction);
                if (dirLenSq > Epsilon)
                {
                    direction = RemoveBackwardComponent(direction, flowDirection);
                    dirLenSq = math.lengthsq(direction);
                    if (dirLenSq > Epsilon)
                    {
                        direction *= math.rsqrt(dirLenSq);
                        float rawPush = localPressure * PressureStrength * DeltaTime;
                        if (rawPush > 0f)
                        {
                            pressureDelta = direction * rawPush;
                            pressureDelta = ApplyAnisotropicPressure(pressureDelta, desiredDir, PressureParallelScale, PressurePerpScale);
                        }
                    }
                }

                bool densityHigh = debugForceTangent || localDensity >= DenseUnitsPerCellThreshold;
                float2 wallNormal = float2.zero;
                float2 tangentDelta = float2.zero;

                if (densityHigh)
                {
                    if (debugForceTangent)
                    {
                        CountDebugCounter(DebugCounterEligible);
                        tangentDelta = ComputeDebugForceTangentDelta(effectiveCap);
                    }
                    else
                    {
                        float wallDistCells;
                        if (TryGetWallNearData(position, out wallNormal, out wallDistCells) && wallDistCells <= WallNearDistanceCells)
                        {
                            CountDebugCounter(DebugCounterEligible);
                            tangentDelta = ComputeWallTangentDelta(wallNormal, desiredDir, flowDirection, entityIndex);
                        }
                    }
                }

                float pressureLenSq = math.lengthsq(pressureDelta);
                if (pressureLenSq > Epsilon)
                {
                    CountDebugCounter(DebugCounterPressureApplied);
                    AccumulateDebugSum(DebugSumPressureMag, math.sqrt(pressureLenSq));
                }

                float tangentLenSq = math.lengthsq(tangentDelta);
                if (tangentLenSq > Epsilon)
                {
                    CountDebugCounter(DebugCounterTangentApplied);
                    AccumulateDebugSum(DebugSumTangentMag, math.sqrt(tangentLenSq));
                }

                float2 totalDelta = pressureDelta + tangentDelta;
                float totalLenSq = math.lengthsq(totalDelta);
                if (totalLenSq > (effectiveCap * effectiveCap) && totalLenSq > Epsilon)
                {
                    totalDelta *= effectiveCap * math.rsqrt(totalLenSq);
                }

                float2 candidate = position + totalDelta;
                if (math.lengthsq(candidate - position) <= Epsilon)
                {
                    return;
                }

                if (!IsWalkableWorld(candidate, ref flow))
                {
                    return;
                }

                CountDebugCounter(DebugCounterFinalDeltaApplied);
                AccumulateDebugSum(DebugSumFinalDeltaMag, math.sqrt(math.lengthsq(candidate - position)));
                transform.Position = new float3(candidate.x, candidate.y, transform.Position.z);
            }

            private float2 ComputeDebugForceTangentDelta(float effectiveCap)
            {
                if (effectiveCap <= 0f)
                {
                    return float2.zero;
                }

                float push = math.min(DebugForceTangentDelta, effectiveCap);
                return new float2(0f, push);
            }

            private float2 ComputeWallTangentDelta(float2 wallNormal, float2 desiredDir, float2 flowDirection, int entityIndex)
            {
                if (WallTangentStrength <= 0f || WallTangentMaxPush <= 0f)
                {
                    return float2.zero;
                }

                float2 tangent = new float2(-wallNormal.y, wallNormal.x);
                float tangentLenSq = math.lengthsq(tangent);
                if (tangentLenSq <= Epsilon)
                {
                    return float2.zero;
                }

                tangent *= math.rsqrt(tangentLenSq);
                float deterministicSign = ((entityIndex & 1) == 0) ? 1f : -1f;
                tangent *= deterministicSign;

                float2 alignDir = flowDirection;
                float alignLenSq = math.lengthsq(alignDir);
                if (alignLenSq <= Epsilon)
                {
                    alignDir = desiredDir;
                    alignLenSq = math.lengthsq(alignDir);
                }

                if (alignLenSq > Epsilon)
                {
                    alignDir *= math.rsqrt(alignLenSq);
                    float alignDot = math.dot(tangent, alignDir);
                    if (alignDot < -TangentSignAlignDotEpsilon)
                    {
                        tangent = -tangent;
                    }
                }

                float tangentPush = math.min(WallTangentStrength * DeltaTime, WallTangentMaxPush);
                if (tangentPush <= 0f)
                {
                    return float2.zero;
                }

                return tangent * tangentPush;
            }

            private void CountDebugCounter(int counterIndex)
            {
                if (WallTangentDebugEnabled == 0 || DebugCountersPerThread.Length <= 0)
                {
                    return;
                }

                if (counterIndex < 0 || counterIndex >= DebugCounterCount)
                {
                    return;
                }

                int workerCount = DebugCountersPerThread.Length / DebugCounterCount;
                int workerIndex = math.clamp(ThreadIndex - 1, 0, workerCount - 1);
                int baseIndex = workerIndex * DebugCounterCount;
                DebugCountersPerThread[baseIndex + counterIndex] = DebugCountersPerThread[baseIndex + counterIndex] + 1;
            }

            private void AccumulateDebugSum(int sumIndex, float value)
            {
                if (WallTangentDebugEnabled == 0 || DebugSumsPerThread.Length <= 0 || value <= 0f)
                {
                    return;
                }

                if (sumIndex < 0 || sumIndex >= DebugSumCount)
                {
                    return;
                }

                int workerCount = DebugSumsPerThread.Length / DebugSumCount;
                int workerIndex = math.clamp(ThreadIndex - 1, 0, workerCount - 1);
                int baseIndex = workerIndex * DebugSumCount;
                DebugSumsPerThread[baseIndex + sumIndex] = DebugSumsPerThread[baseIndex + sumIndex] + value;
            }

            private float2 ResolveDesiredDirection(float2 goalIntentDirection, float2 velocity, float2 flowDirection)
            {
                float2 d = NormalizeIfFinite(goalIntentDirection);
                if (math.lengthsq(d) > Epsilon)
                {
                    return d;
                }

                d = NormalizeIfFinite(flowDirection);
                if (math.lengthsq(d) > Epsilon)
                {
                    return d;
                }

                return NormalizeIfFinite(velocity);
            }

            private static float2 ApplyAnisotropicPressure(float2 force, float2 desiredDir, float parallelScale, float perpScale)
            {
                float desiredLenSq = math.lengthsq(desiredDir);
                if (desiredLenSq <= Epsilon)
                {
                    return force;
                }

                float2 desiredN = desiredDir * math.rsqrt(desiredLenSq);
                float2 parallel = math.dot(force, desiredN) * desiredN;
                float2 perp = force - parallel;
                return (parallel * parallelScale) + (perp * perpScale);
            }

            private bool TryGetWallNearData(float2 worldPosition, out float2 wallNormal, out float wallDistCells)
            {
                wallNormal = float2.zero;
                wallDistCells = float.MaxValue;
                if (HasWallField == 0 || !Wall.IsCreated)
                {
                    CountDebugCounter(DebugCounterInvalidWallSample);
                    return false;
                }

                ref WallFieldBlob wall = ref Wall.Value;
                int2 wallCell = WorldToWallGrid(worldPosition, ref wall);
                if (!IsInWallBounds(wallCell, ref wall))
                {
                    CountDebugCounter(DebugCounterInvalidWallSample);
                    return false;
                }

                int wallIndex = wallCell.x + (wallCell.y * wall.Width);
                if (wallIndex < 0 || wallIndex >= wall.Dist.Length || wallIndex >= wall.Dir.Length)
                {
                    CountDebugCounter(DebugCounterInvalidWallSample);
                    return false;
                }

                ushort dist = wall.Dist[wallIndex];
                if (dist == ushort.MaxValue)
                {
                    CountDebugCounter(DebugCounterInvalidWallSample);
                    return false;
                }

                wallDistCells = dist;
                byte dir = wall.Dir[wallIndex];
                if (dir >= wall.DirLut.Length)
                {
                    CountDebugCounter(DebugCounterInvalidWallSample);
                    return false;
                }

                float2 n = wall.DirLut[dir];
                float nLenSq = math.lengthsq(n);
                if (nLenSq <= Epsilon || !math.isfinite(nLenSq))
                {
                    CountDebugCounter(DebugCounterInvalidWallSample);
                    return false;
                }

                wallNormal = n * math.rsqrt(nLenSq);
                return true;
            }

            private float2 ResolvePressureDirection(int2 cell, float2 worldPosition, int entityIndex, float localPressure, ref FlowFieldBlob flow)
            {
                int x = cell.x;
                int y = cell.y;

                float pL = SamplePressure(x - 1, y, localPressure, ref flow);
                float pR = SamplePressure(x + 1, y, localPressure, ref flow);
                float pD = SamplePressure(x, y - 1, localPressure, ref flow);
                float pU = SamplePressure(x, y + 1, localPressure, ref flow);
                float2 gradient = new float2(pR - pL, pU - pD);
                if (math.lengthsq(gradient) > Epsilon)
                {
                    return AddSpreadBias(-gradient, cell, worldPosition, entityIndex, ref flow);
                }

                float bestPressure = localPressure;
                float2 bestDirection = float2.zero;
                for (int n = 0; n < 8; n++)
                {
                    int2 offset = NeighborOffset8(n);
                    int nx = x + offset.x;
                    int ny = y + offset.y;
                    if (nx < 0 || ny < 0 || nx >= flow.Width || ny >= flow.Height)
                    {
                        continue;
                    }

                    int nIndex = nx + (ny * flow.Width);
                    if (nIndex < 0 || nIndex >= Pressure.Length)
                    {
                        continue;
                    }

                    if (flow.Dist[nIndex] == ushort.MaxValue)
                    {
                        continue;
                    }

                    float p = Pressure[nIndex];
                    if (p < bestPressure)
                    {
                        bestPressure = p;
                        bestDirection = NeighborDirection8(n);
                    }
                }

                if (bestPressure < localPressure - Epsilon)
                {
                    return AddSpreadBias(bestDirection, cell, worldPosition, entityIndex, ref flow);
                }

                float2 wallGradient = float2.zero;
                for (int oy = -1; oy <= 1; oy++)
                {
                    int ny = y + oy;
                    if (ny < 0 || ny >= flow.Height)
                    {
                        continue;
                    }

                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox;
                        if (nx < 0 || nx >= flow.Width || (ox == 0 && oy == 0))
                        {
                            continue;
                        }

                        int nIndex = nx + (ny * flow.Width);
                        if (nIndex >= 0 && nIndex < flow.Dist.Length && flow.Dist[nIndex] == ushort.MaxValue)
                        {
                            wallGradient += new float2(ox, oy) * BlockedPenalty;
                        }
                    }
                }

                if (math.lengthsq(wallGradient) > Epsilon)
                {
                    return AddSpreadBias(-wallGradient, cell, worldPosition, entityIndex, ref flow);
                }

                return DeterministicUnitDir(entityIndex, cell);
            }

            private float2 AddSpreadBias(float2 baseDirection, int2 cell, float2 worldPosition, int entityIndex, ref FlowFieldBlob flow)
            {
                float2 cellCenter = flow.OriginWorld + ((new float2(cell.x + 0.5f, cell.y + 0.5f)) * flow.CellSize);
                float2 radial = worldPosition - cellCenter;
                float radialLenSq = math.lengthsq(radial);
                if (radialLenSq > Epsilon)
                {
                    radial *= math.rsqrt(radialLenSq);
                }
                else
                {
                    radial = float2.zero;
                }

                float2 jitter = DeterministicUnitDir(entityIndex, cell);
                return baseDirection + (radial * 0.2f) + (jitter * 0.15f);
            }

            private float2 ResolveFlowDirection(int flowIndex, float2 worldPosition, ref FlowFieldBlob flow)
            {
                if (flowIndex >= 0 && flowIndex < flow.Dir.Length)
                {
                    byte dir = flow.Dir[flowIndex];
                    if (dir < flow.DirLut.Length)
                    {
                        return flow.DirLut[dir];
                    }
                }

                float2 toCenter = CenterWorld - worldPosition;
                float lenSq = math.lengthsq(toCenter);
                if (lenSq <= Epsilon)
                {
                    return float2.zero;
                }

                return toCenter * math.rsqrt(lenSq);
            }

            private static float2 RemoveBackwardComponent(float2 direction, float2 flowDirection)
            {
                float flowLenSq = math.lengthsq(flowDirection);
                if (flowLenSq <= Epsilon)
                {
                    return direction;
                }

                float2 flowN = flowDirection * math.rsqrt(flowLenSq);
                float flowDot = math.dot(direction, flowN);
                if (flowDot >= 0f)
                {
                    return direction;
                }

                return direction - (flowN * flowDot);
            }

            private static float2 NormalizeIfFinite(float2 v)
            {
                float lenSq = math.lengthsq(v);
                if (lenSq <= Epsilon || !math.isfinite(lenSq))
                {
                    return float2.zero;
                }

                return v * math.rsqrt(lenSq);
            }

            private static float2 DeterministicUnitDir(int entityIndex, int2 cell)
            {
                uint h = (uint)entityIndex;
                h ^= (uint)(cell.x * 0x9E3779B9);
                h ^= (uint)(cell.y * 0x85EBCA6B);
                h ^= h >> 16;
                h *= 0x7FEB352D;
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

            private static int2 NeighborOffset8(int index)
            {
                switch (index)
                {
                    case 0: return new int2(0, 1);
                    case 1: return new int2(1, 1);
                    case 2: return new int2(1, 0);
                    case 3: return new int2(1, -1);
                    case 4: return new int2(0, -1);
                    case 5: return new int2(-1, -1);
                    case 6: return new int2(-1, 0);
                    default: return new int2(-1, 1);
                }
            }

            private static float2 NeighborDirection8(int index)
            {
                switch (index)
                {
                    case 0: return new float2(0f, 1f);
                    case 1: return new float2(Diagonal, Diagonal);
                    case 2: return new float2(1f, 0f);
                    case 3: return new float2(Diagonal, -Diagonal);
                    case 4: return new float2(0f, -1f);
                    case 5: return new float2(-Diagonal, -Diagonal);
                    case 6: return new float2(-1f, 0f);
                    default: return new float2(-Diagonal, Diagonal);
                }
            }

            private float SamplePressure(int x, int y, float fallbackPressure, ref FlowFieldBlob flow)
            {
                if (x < 0 || y < 0 || x >= flow.Width || y >= flow.Height)
                {
                    return fallbackPressure + BlockedPenalty;
                }

                int index = x + (y * flow.Width);
                if (index < 0 || index >= Pressure.Length || index >= flow.Dist.Length)
                {
                    return fallbackPressure + BlockedPenalty;
                }

                if (flow.Dist[index] == ushort.MaxValue)
                {
                    return fallbackPressure + BlockedPenalty;
                }

                return Pressure[index];
            }

            private bool IsWalkableWorld(float2 world, ref FlowFieldBlob flow)
            {
                int2 grid = WorldToFlowGrid(world, ref flow);
                if (!IsInFlowBounds(grid, ref flow))
                {
                    return true;
                }

                int index = grid.x + (grid.y * flow.Width);
                if (index < 0 || index >= flow.Dist.Length)
                {
                    return false;
                }

                return flow.Dist[index] != ushort.MaxValue;
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
}
