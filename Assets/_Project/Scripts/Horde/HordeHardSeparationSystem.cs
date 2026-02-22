using Project.Map;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(HordePressureFieldSystem))]
    [UpdateAfter(typeof(HordeSeparationSystem))]
    [UpdateAfter(typeof(ZombieSteeringSystem))]
    [UpdateBefore(typeof(WallRepulsionSystem))]
    public partial struct HordeHardSeparationSystem : ISystem
    {
        private const int HardDebugSampleStride = 32;
        private const float HardAppliedEpsSq = 1e-10f;
        private const float DefaultSlowSpeedFraction = 0.2f;

        private static bool s_loggedRunning;
        private static bool s_loggedGridCapacity;
        private static readonly ProfilerMarker BuildGridMarker = new ProfilerMarker("HordeHardSeparation.BuildGrid");
        private static readonly ProfilerMarker IterationComputeMarker = new ProfilerMarker("HordeHardSeparation.IterationCompute");
        private static readonly ProfilerMarker IterationApplyMarker = new ProfilerMarker("HordeHardSeparation.IterationApply");
        private static readonly ProfilerMarker WriteBackMarker = new ProfilerMarker("HordeHardSeparation.WriteBack");

        private EntityQuery _zombieQuery;
        private EntityQuery _pressureBufferQuery;
        private Entity _debugStatsEntity;
        private BufferLookup<PressureCell> _pressureLookup;
        private ComponentLookup<HordeHardSeparationDebugStats> _debugStatsLookup;
        private NativeArray<float> _pressureSnapshot;
        private NativeArray<int> _debugSampledPerThread;
        private NativeArray<int> _debugAppliedPerThread;
        private NativeArray<float> _debugSumDeltaPerThread;
        private int _debugWorkerCount;
        private NativeParallelHashMap<Entity, float2> _previousSampledPositions;
        private NativeList<Entity> _entities;
        private NativeList<float> _moveSpeeds;
        private NativeList<byte> _jamMask;
        private NativeList<float3> _positionsA;
        private NativeList<float3> _positionsB;
        private NativeList<float3> _deltas;
        private NativeParallelMultiHashMap<int, int> _gridA;
        private NativeParallelMultiHashMap<int, int> _gridB;

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ZombieTag>(),
                ComponentType.ReadOnly<ZombieMoveSpeed>(),
                ComponentType.ReadWrite<LocalTransform>());

            _entities = new NativeList<Entity>(1024, Allocator.Persistent);
            _moveSpeeds = new NativeList<float>(1024, Allocator.Persistent);
            _jamMask = new NativeList<byte>(1024, Allocator.Persistent);
            _positionsA = new NativeList<float3>(1024, Allocator.Persistent);
            _positionsB = new NativeList<float3>(1024, Allocator.Persistent);
            _deltas = new NativeList<float3>(1024, Allocator.Persistent);
            _gridA = new NativeParallelMultiHashMap<int, int>(2048, Allocator.Persistent);
            _gridB = new NativeParallelMultiHashMap<int, int>(2048, Allocator.Persistent);
            _previousSampledPositions = new NativeParallelHashMap<Entity, float2>(8192, Allocator.Persistent);
            _pressureLookup = state.GetBufferLookup<PressureCell>(true);
            _debugStatsLookup = state.GetComponentLookup<HordeHardSeparationDebugStats>(false);
            _pressureBufferQuery = state.GetEntityQuery(ComponentType.ReadOnly<PressureFieldBufferTag>());
            _debugWorkerCount = math.max(1, JobsUtility.MaxJobThreadCount);
            _debugSampledPerThread = new NativeArray<int>(_debugWorkerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _debugAppliedPerThread = new NativeArray<int>(_debugWorkerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _debugSumDeltaPerThread = new NativeArray<float>(_debugWorkerCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeHardSeparationConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity configEntity = state.EntityManager.CreateEntity(typeof(HordeHardSeparationConfig));
                state.EntityManager.SetComponentData(configEntity, new HordeHardSeparationConfig
                {
                    Enabled = 1,
                    JamOnly = 1,
                    JamPressureThreshold = 8f,
                    DensePressureThreshold = 13f,
                    SlowSpeedFraction = DefaultSlowSpeedFraction,
                    IterationsJam = 3,
                    MaxNeighborsJam = 32,
                    MaxPushPerFrameJam = 0.001f,
                    Radius = 0.4f,
                    CellSize = 0.40f,
                    MaxNeighbors = 28,
                    Iterations = 2,
                    MaxCorrectionPerIter = 0.001f,
                    Slop = 0.001f
                });
            }

            EntityQuery debugStatsQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordeHardSeparationDebugStats>());
            if (debugStatsQuery.IsEmptyIgnoreFilter)
            {
                _debugStatsEntity = state.EntityManager.CreateEntity(typeof(HordeHardSeparationDebugStats));
                state.EntityManager.SetComponentData(_debugStatsEntity, default(HordeHardSeparationDebugStats));
            }
            else
            {
                _debugStatsEntity = debugStatsQuery.GetSingletonEntity();
            }

            state.RequireForUpdate(_zombieQuery);
            state.RequireForUpdate<FlowFieldSingleton>();
            state.RequireForUpdate<HordePressureConfig>();
            state.RequireForUpdate<HordeHardSeparationConfig>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_entities.IsCreated)
            {
                _entities.Dispose();
            }

            if (_moveSpeeds.IsCreated)
            {
                _moveSpeeds.Dispose();
            }

            if (_jamMask.IsCreated)
            {
                _jamMask.Dispose();
            }

            if (_positionsA.IsCreated)
            {
                _positionsA.Dispose();
            }

            if (_positionsB.IsCreated)
            {
                _positionsB.Dispose();
            }

            if (_deltas.IsCreated)
            {
                _deltas.Dispose();
            }

            if (_gridA.IsCreated)
            {
                _gridA.Dispose();
            }

            if (_gridB.IsCreated)
            {
                _gridB.Dispose();
            }

            if (_previousSampledPositions.IsCreated)
            {
                _previousSampledPositions.Dispose();
            }

            if (_pressureSnapshot.IsCreated)
            {
                _pressureSnapshot.Dispose();
            }

            if (_debugSampledPerThread.IsCreated)
            {
                _debugSampledPerThread.Dispose();
            }

            if (_debugAppliedPerThread.IsCreated)
            {
                _debugAppliedPerThread.Dispose();
            }

            if (_debugSumDeltaPerThread.IsCreated)
            {
                _debugSumDeltaPerThread.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton(out HordeHardSeparationConfig config))
            {
                return;
            }

            HordePressureConfig pressureConfig = SystemAPI.GetSingleton<HordePressureConfig>();
            config = SanitizeConfig(config);
            if (config.Enabled == 0)
            {
                return;
            }

            if (!s_loggedRunning)
            {
                UnityEngine.Debug.Log("HordeHardSeparationSystem running (Enabled=1).");
                s_loggedRunning = true;
            }

            int count = _zombieQuery.CalculateEntityCount();
            if (count <= 1)
            {
                return;
            }

            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            EnsureDebugCounterCapacity(ref state);

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

            int iterations = config.JamOnly != 0 ? config.IterationsJam : config.Iterations;
            int maxNeighbors = config.JamOnly != 0 ? config.MaxNeighborsJam : config.MaxNeighbors;
            float maxCorrectionPerIter = config.JamOnly != 0 ? config.MaxPushPerFrameJam : config.MaxCorrectionPerIter;
            float jamPressureThreshold = config.JamPressureThreshold > 0f
                ? config.JamPressureThreshold
                : math.max(0f, pressureConfig.BackpressureThreshold);
            float densePressureThreshold = config.DensePressureThreshold > 0f
                ? config.DensePressureThreshold
                : jamPressureThreshold;
            float slowSpeedFraction = config.SlowSpeedFraction > 0f
                ? math.clamp(config.SlowSpeedFraction, 0f, 1f)
                : DefaultSlowSpeedFraction;
            float minDist = config.Radius * 2f;
            float minDistSq = minDist * minDist;
            float invCellSize = 1f / config.CellSize;
            const int gridInsertsPerEntity = 1;
            int expectedGridAdds = math.max(1, count * gridInsertsPerEntity);
            int requiredGridCapacity = math.ceilpow2(math.max(1, expectedGridAdds * 2));

            EnsureCapacity(count, requiredGridCapacity);
            EnsurePressureSnapshotCapacity(ref state, flowCellCount);
            _entities.ResizeUninitialized(count);
            _moveSpeeds.ResizeUninitialized(count);
            _jamMask.ResizeUninitialized(count);
            _positionsA.ResizeUninitialized(count);
            _positionsB.ResizeUninitialized(count);
            _deltas.ResizeUninitialized(count);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!s_loggedGridCapacity)
            {
                UnityEngine.Debug.Log(
                    $"[HordeHardSeparation] gridCapacity count={count} gridCapacity={_gridA.Capacity} " +
                    $"expectedAdds={expectedGridAdds} insertsPerEntity={gridInsertsPerEntity}");
                s_loggedGridCapacity = true;
            }
#endif

            GatherPositionsJob gatherJob = new GatherPositionsJob
            {
                Entities = _entities.AsArray(),
                MoveSpeeds = _moveSpeeds.AsArray(),
                Positions = _positionsA.AsArray()
            };
            state.Dependency = gatherJob.ScheduleParallel(_zombieQuery, state.Dependency);

            _pressureLookup.Update(ref state);
            Entity pressureFieldEntity = _pressureBufferQuery.IsEmptyIgnoreFilter
                ? Entity.Null
                : _pressureBufferQuery.GetSingletonEntity();

            CopyPressureSnapshotJob copyPressureJob = new CopyPressureSnapshotJob
            {
                PressureLookup = _pressureLookup,
                PressureFieldEntity = pressureFieldEntity,
                PressureSnapshot = _pressureSnapshot,
                FlowCellCount = flowCellCount
            };
            state.Dependency = copyPressureJob.Schedule(state.Dependency);

            BuildJamMaskJob buildJamMaskJob = new BuildJamMaskJob
            {
                Entities = _entities.AsArray(),
                Positions = _positionsA.AsArray(),
                MoveSpeeds = _moveSpeeds.AsArray(),
                PreviousSampledPositions = _previousSampledPositions,
                JamMask = _jamMask.AsArray(),
                Flow = flowSingleton.Blob,
                PressureSnapshot = _pressureSnapshot,
                JamOnly = config.JamOnly,
                PressureEnabled = pressureConfig.Enabled,
                JamPressureThreshold = jamPressureThreshold,
                DensePressureThreshold = densePressureThreshold,
                DeltaTimeWindow = deltaTime,
                SlowSpeedFraction = slowSpeedFraction
            };
            state.Dependency = buildJamMaskJob.Schedule(count, 128, state.Dependency);

            NativeArray<float3> posRead = _positionsA.AsArray();
            NativeArray<float3> posWrite = _positionsB.AsArray();
            NativeArray<float3> deltas = _deltas.AsArray();
            NativeArray<byte> jamMask = _jamMask.AsArray();

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                NativeParallelMultiHashMap<int, int> grid = (iteration & 1) == 0 ? _gridA : _gridB;
                ClearGridJob clearGridJob = new ClearGridJob
                {
                    Grid = grid
                };
                state.Dependency = clearGridJob.Schedule(state.Dependency);

                using (BuildGridMarker.Auto())
                {
                    BuildGridJob buildGridJob = new BuildGridJob
                    {
                        Positions = posRead,
                        Grid = grid.AsParallelWriter(),
                        InvCellSize = invCellSize
                    };
                    state.Dependency = buildGridJob.Schedule(count, 128, state.Dependency);
                }

                using (IterationComputeMarker.Auto())
                {
                    ComputeDeltaJob computeDeltaJob = new ComputeDeltaJob
                    {
                        Iteration = iteration,
                        Positions = posRead,
                        Grid = grid,
                        Delta = deltas,
                        JamMask = jamMask,
                        JamOnly = config.JamOnly,
                        InvCellSize = invCellSize,
                        MinDist = minDist,
                        MinDistSq = minDistSq,
                        MaxNeighbors = maxNeighbors,
                        MaxCorrectionPerIter = maxCorrectionPerIter,
                        Slop = config.Slop
                    };
                    state.Dependency = computeDeltaJob.Schedule(count, 128, state.Dependency);
                }

                using (IterationApplyMarker.Auto())
                {
                    ApplyDeltaJob applyDeltaJob = new ApplyDeltaJob
                    {
                        PositionsRead = posRead,
                        Delta = deltas,
                        PositionsWrite = posWrite
                    };
                    state.Dependency = applyDeltaJob.Schedule(count, 128, state.Dependency);
                }

                NativeArray<float3> swap = posRead;
                posRead = posWrite;
                posWrite = swap;
            }

            int debugThreadLen = _debugSampledPerThread.Length;
            ClearHardDebugCountersJob clearHardDebugCountersJob = new ClearHardDebugCountersJob
            {
                SampledPerThread = _debugSampledPerThread,
                AppliedPerThread = _debugAppliedPerThread,
                SumDeltaPerThread = _debugSumDeltaPerThread
            };
            state.Dependency = clearHardDebugCountersJob.Schedule(debugThreadLen, 64, state.Dependency);

            SampleHardDeltaDebugJob sampleHardDeltaDebugJob = new SampleHardDeltaDebugJob
            {
                Delta = _deltas.AsArray(),
                SampledPerThread = _debugSampledPerThread,
                AppliedPerThread = _debugAppliedPerThread,
                SumDeltaPerThread = _debugSumDeltaPerThread,
                WorkerCount = debugThreadLen,
                SampleStride = HardDebugSampleStride,
                AppliedEpsSq = HardAppliedEpsSq
            };
            state.Dependency = sampleHardDeltaDebugJob.Schedule(count, 128, state.Dependency);

            _debugStatsLookup.Update(ref state);
            ReduceHardDebugStatsJob reduceHardDebugStatsJob = new ReduceHardDebugStatsJob
            {
                SampledPerThread = _debugSampledPerThread,
                AppliedPerThread = _debugAppliedPerThread,
                SumDeltaPerThread = _debugSumDeltaPerThread,
                DebugStatsLookup = _debugStatsLookup,
                DebugStatsEntity = _debugStatsEntity,
                LastDt = deltaTime,
                WorkerCount = debugThreadLen
            };
            state.Dependency = reduceHardDebugStatsJob.Schedule(state.Dependency);

            using (WriteBackMarker.Auto())
            {
                WriteBackJob writeBackJob = new WriteBackJob
                {
                    Positions = posRead
                };
                state.Dependency = writeBackJob.ScheduleParallel(_zombieQuery, state.Dependency);
            }

            ClearSampledMapJob clearSampledMapJob = new ClearSampledMapJob
            {
                Map = _previousSampledPositions
            };
            state.Dependency = clearSampledMapJob.Schedule(state.Dependency);

            StoreSampledPositionsJob storeSampledJob = new StoreSampledPositionsJob
            {
                Entities = _entities.AsArray(),
                Positions = posRead,
                Writer = _previousSampledPositions.AsParallelWriter()
            };
            state.Dependency = storeSampledJob.Schedule(count, 128, state.Dependency);
        }

        private void EnsureCapacity(int count, int requiredGridCapacity)
        {
            int target = math.ceilpow2(count);
            if (_entities.Capacity < target)
            {
                _entities.Capacity = target;
            }

            if (_moveSpeeds.Capacity < target)
            {
                _moveSpeeds.Capacity = target;
            }

            if (_jamMask.Capacity < target)
            {
                _jamMask.Capacity = target;
            }

            if (_positionsA.Capacity < target)
            {
                _positionsA.Capacity = target;
            }

            if (_positionsB.Capacity < target)
            {
                _positionsB.Capacity = target;
            }

            if (_deltas.Capacity < target)
            {
                _deltas.Capacity = target;
            }

            if (_gridA.Capacity < requiredGridCapacity)
            {
                _gridA.Capacity = requiredGridCapacity;
            }

            if (_gridB.Capacity < requiredGridCapacity)
            {
                _gridB.Capacity = requiredGridCapacity;
            }

            int sampledCapacity = math.max(8192, target * 2);
            if (_previousSampledPositions.Capacity < sampledCapacity)
            {
                _previousSampledPositions.Capacity = sampledCapacity;
            }
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

        private void EnsureDebugCounterCapacity(ref SystemState state)
        {
            int desired = math.max(1, JobsUtility.MaxJobThreadCount);
            if (desired == _debugWorkerCount &&
                _debugSampledPerThread.IsCreated &&
                _debugSampledPerThread.Length == desired &&
                _debugAppliedPerThread.IsCreated &&
                _debugAppliedPerThread.Length == desired &&
                _debugSumDeltaPerThread.IsCreated &&
                _debugSumDeltaPerThread.Length == desired)
            {
                return;
            }

            JobHandle disposeHandle = state.Dependency;
            disposeHandle = DisposeIfCreated(_debugSampledPerThread, disposeHandle);
            disposeHandle = DisposeIfCreated(_debugAppliedPerThread, disposeHandle);
            disposeHandle = DisposeIfCreated(_debugSumDeltaPerThread, disposeHandle);
            state.Dependency = disposeHandle;

            _debugSampledPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _debugAppliedPerThread = new NativeArray<int>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _debugSumDeltaPerThread = new NativeArray<float>(desired, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _debugWorkerCount = desired;
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

        private static HordeHardSeparationConfig SanitizeConfig(HordeHardSeparationConfig config)
        {
            config.Enabled = config.Enabled != 0 ? (byte)1 : (byte)0;
            config.JamOnly = config.JamOnly != 0 ? (byte)1 : (byte)0;
            config.JamPressureThreshold = math.max(0f, config.JamPressureThreshold);
            config.DensePressureThreshold = math.max(0f, config.DensePressureThreshold);
            config.SlowSpeedFraction = config.SlowSpeedFraction > 0f
                ? math.clamp(config.SlowSpeedFraction, 0f, 1f)
                : DefaultSlowSpeedFraction;
            config.IterationsJam = math.clamp(config.IterationsJam, 1, 3);
            config.MaxNeighborsJam = math.clamp(config.MaxNeighborsJam, 1, 32);
            config.Radius = math.max(0.001f, config.Radius);
            config.CellSize = math.max(0.001f, config.CellSize);
            config.MaxNeighbors = math.clamp(config.MaxNeighbors, 1, 32);
            config.Iterations = math.clamp(config.Iterations, 1, 2);
            config.MaxCorrectionPerIter = math.max(0f, config.MaxCorrectionPerIter);
            config.MaxPushPerFrameJam = math.max(0f, config.MaxPushPerFrameJam);
            if (config.MaxPushPerFrameJam <= 0f)
            {
                config.MaxPushPerFrameJam = config.MaxCorrectionPerIter;
            }
            config.Slop = math.max(0f, config.Slop);
            return config;
        }

        [BurstCompile]
        private partial struct GatherPositionsJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public NativeArray<Entity> Entities;
            [NativeDisableParallelForRestriction] public NativeArray<float> MoveSpeeds;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Positions;

            private void Execute(Entity entity, [EntityIndexInQuery] int index, in ZombieTag tag, in LocalTransform transform, in ZombieMoveSpeed moveSpeed)
            {
                Entities[index] = entity;
                MoveSpeeds[index] = math.max(0f, moveSpeed.Value);
                Positions[index] = transform.Position;
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
        private struct BuildJamMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<float> MoveSpeeds;
            [ReadOnly] public NativeParallelHashMap<Entity, float2> PreviousSampledPositions;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            [ReadOnly] public NativeArray<float> PressureSnapshot;
            [NativeDisableParallelForRestriction] public NativeArray<byte> JamMask;
            public byte JamOnly;
            public byte PressureEnabled;
            public float JamPressureThreshold;
            public float DensePressureThreshold;
            public float DeltaTimeWindow;
            public float SlowSpeedFraction;

            public void Execute(int index)
            {
                if (JamOnly == 0)
                {
                    JamMask[index] = 1;
                    return;
                }

                float2 pos = Positions[index].xy;
                float localPressure = ResolveLocalPressure(pos);
                bool slow = false;
                if (PreviousSampledPositions.TryGetValue(Entities[index], out float2 previousPos))
                {
                    float dist = math.distance(pos, previousPos);
                    float speed = dist / math.max(1e-5f, DeltaTimeWindow);
                    slow = speed < (MoveSpeeds[index] * SlowSpeedFraction);
                }

                bool jam = PressureEnabled != 0 &&
                    ((localPressure > JamPressureThreshold) || ((localPressure > DensePressureThreshold) && slow));
                JamMask[index] = jam ? (byte)1 : (byte)0;
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
        private struct ClearHardDebugCountersJob : IJobParallelFor
        {
            public NativeArray<int> SampledPerThread;
            public NativeArray<int> AppliedPerThread;
            public NativeArray<float> SumDeltaPerThread;

            public void Execute(int index)
            {
                SampledPerThread[index] = 0;
                AppliedPerThread[index] = 0;
                SumDeltaPerThread[index] = 0f;
            }
        }

        [BurstCompile]
        private struct SampleHardDeltaDebugJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Delta;
            [NativeDisableParallelForRestriction] public NativeArray<int> SampledPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> AppliedPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<float> SumDeltaPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;
            public int WorkerCount;
            public int SampleStride;
            public float AppliedEpsSq;

            public void Execute(int index)
            {
                if ((index % SampleStride) != 0)
                {
                    return;
                }

                int workerIndex = math.clamp(ThreadIndex - 1, 0, WorkerCount - 1);
                SampledPerThread[workerIndex] = SampledPerThread[workerIndex] + 1;

                float2 delta = Delta[index].xy;
                float lenSq = math.lengthsq(delta);
                if (lenSq > AppliedEpsSq)
                {
                    AppliedPerThread[workerIndex] = AppliedPerThread[workerIndex] + 1;
                    SumDeltaPerThread[workerIndex] = SumDeltaPerThread[workerIndex] + math.sqrt(lenSq);
                }
            }
        }

        [BurstCompile]
        private struct ReduceHardDebugStatsJob : IJob
        {
            [ReadOnly] public NativeArray<int> SampledPerThread;
            [ReadOnly] public NativeArray<int> AppliedPerThread;
            [ReadOnly] public NativeArray<float> SumDeltaPerThread;
            [NativeDisableParallelForRestriction] public ComponentLookup<HordeHardSeparationDebugStats> DebugStatsLookup;
            public Entity DebugStatsEntity;
            public float LastDt;
            public int WorkerCount;

            public void Execute()
            {
                if (!DebugStatsLookup.HasComponent(DebugStatsEntity))
                {
                    return;
                }

                int sampled = 0;
                int applied = 0;
                float sumDelta = 0f;
                for (int i = 0; i < WorkerCount; i++)
                {
                    sampled += SampledPerThread[i];
                    applied += AppliedPerThread[i];
                    sumDelta += SumDeltaPerThread[i];
                }

                DebugStatsLookup[DebugStatsEntity] = new HordeHardSeparationDebugStats
                {
                    Sampled = sampled,
                    Applied = applied,
                    SumDelta = sumDelta,
                    LastDt = LastDt
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
            [ReadOnly] public NativeArray<float3> Positions;
            public NativeParallelHashMap<Entity, float2>.ParallelWriter Writer;

            public void Execute(int index)
            {
                Writer.TryAdd(Entities[index], Positions[index].xy);
            }
        }

        [BurstCompile]
        private struct ClearGridJob : IJob
        {
            public NativeParallelMultiHashMap<int, int> Grid;

            public void Execute()
            {
                Grid.Clear();
            }
        }

        [BurstCompile]
        private struct BuildGridJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Grid;
            public float InvCellSize;

            public void Execute(int index)
            {
                int2 cell = (int2)math.floor(Positions[index].xy * InvCellSize);
                Grid.Add(HashCell(cell.x, cell.y), index);
            }
        }

        [BurstCompile]
        private struct ComputeDeltaJob : IJobParallelFor
        {
            private const float ZeroDistanceSq = 1e-12f;
            private const float Diagonal = 0.70710677f;

            public int Iteration;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
            [ReadOnly] public NativeArray<byte> JamMask;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Delta;
            public byte JamOnly;
            public float InvCellSize;
            public float MinDist;
            public float MinDistSq;
            public int MaxNeighbors;
            public float MaxCorrectionPerIter;
            public float Slop;

            public void Execute(int index)
            {
                if (JamOnly != 0 && JamMask[index] == 0)
                {
                    Delta[index] = float3.zero;
                    return;
                }

                float3 pos = Positions[index];
                int2 cell = (int2)math.floor(pos.xy * InvCellSize);
                float2 corr = float2.zero;
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

                            if (processed >= MaxNeighbors)
                            {
                                reachedCap = true;
                                break;
                            }

                            processed++;
                            float2 d = pos.xy - Positions[neighborIndex].xy;
                            float distSq = math.lengthsq(d);
                            if (distSq >= MinDistSq)
                            {
                                continue;
                            }

                            float2 normal;
                            float dist;
                            if (distSq <= ZeroDistanceSq)
                            {
                                normal = DeterministicNormal(index, Iteration);
                                dist = 0f;
                            }
                            else
                            {
                                float invDist = math.rsqrt(distSq);
                                dist = distSq * invDist;
                                normal = d * invDist;
                            }

                            float penetration = MinDist - dist;
                            if (penetration <= Slop)
                            {
                                continue;
                            }

                            corr += normal * (0.5f * penetration);
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

                float corrLenSq = math.lengthsq(corr);
                if (corrLenSq > 0f && MaxCorrectionPerIter > 0f)
                {
                    float maxSq = MaxCorrectionPerIter * MaxCorrectionPerIter;
                    if (corrLenSq > maxSq)
                    {
                        corr *= MaxCorrectionPerIter * math.rsqrt(corrLenSq);
                    }
                }
                else
                {
                    corr = float2.zero;
                }

                Delta[index] = new float3(corr.x, corr.y, 0f);
            }

            private static float2 DeterministicNormal(int index, int iteration)
            {
                uint h = ((uint)index * 0x9E3779B9u) ^ ((uint)iteration * 0x85EBCA6Bu);
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
        private struct ApplyDeltaJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> PositionsRead;
            [ReadOnly] public NativeArray<float3> Delta;
            [NativeDisableParallelForRestriction] public NativeArray<float3> PositionsWrite;

            public void Execute(int index)
            {
                PositionsWrite[index] = PositionsRead[index] + Delta[index];
            }
        }

        [BurstCompile]
        private partial struct WriteBackJob : IJobEntity
        {
            [ReadOnly] public NativeArray<float3> Positions;

            private void Execute([EntityIndexInQuery] int index, ref LocalTransform transform, in ZombieTag tag)
            {
                transform.Position = Positions[index];
            }
        }

        [BurstCompile]
        private static int HashCell(int x, int y)
        {
            return (x * 73856093) ^ (y * 19349663);
        }
    }
}
