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
    [UpdateBefore(typeof(HordeSeparationSystem))]
    [UpdateBefore(typeof(HordeHardSeparationSystem))]
    [UpdateBefore(typeof(HordeTuningQuickMetricsSystem))]
    public partial struct HordePressureFieldSystem : ISystem
    {
        private const float Epsilon = 1e-6f;
        private const float Diagonal = 0.70710677f;

        private NativeArray<int> _density;
        private NativeArray<int> _densityPerThread;
        private int _workerCount;
        private NativeArray<float> _pressureA;
        private NativeArray<float> _pressureB;
        private int _cellCount;
        private int _frameIndex;
        private byte _activePressureBuffer;
        private Entity _pressureFieldEntity;
        private EntityQuery _pressureBufferQuery;
        private BufferLookup<PressureCell> _pressureLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ZombieTag>();
            state.RequireForUpdate<ZombieMoveSpeed>();
            state.RequireForUpdate<FlowFieldSingleton>();
            state.RequireForUpdate<MapRuntimeData>();

            EntityQuery configQuery = state.GetEntityQuery(ComponentType.ReadWrite<HordePressureConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                Entity configEntity = state.EntityManager.CreateEntity(typeof(HordePressureConfig));
                state.EntityManager.SetComponentData(configEntity, new HordePressureConfig
                {
                    Enabled = 1,

                    // Pressure aktiveras tidigt nog för att motverka jam vid punktmål
                    TargetUnitsPerCell = 2.0f,

                    // Mycket lägre än 10: pressure ska inte kännas som en separat motor
                    PressureStrength = 0.60f,

                    // Sätt högt så att SpeedFractionCap blir den verkliga begränsningen
                    // (då blir beteendet mer förutsägbart)
                    MaxPushPerFrame = 1.0f,

                    // Pressure får bara använda en del av moveSpeed*dt-budgeten
                    SpeedFractionCap = 0.30f,

                    // Tuning rule: keep free-flow speed at 1.0 until pressure exceeds threshold.
                    BackpressureThreshold = 4.0f,
                    MinSpeedFactor = 0.30f,
                    BackpressureK = 0.40f,
                    BackpressureMaxFactor = 1.0f,

                    // Lägre för att undvika "väggmagnetism"
                    BlockedCellPenalty = 3.0f,

                    FieldUpdateIntervalFrames = 1,
                    BlurPasses = 1,

                    // Augment mode: körs tillsammans med separation
                    DisablePairwiseSeparationWhenPressureEnabled = 0
                });
            }

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
            state.RequireForUpdate<HordePressureConfig>();
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

            _cellCount = 0;
            _workerCount = 0;
            _frameIndex = 0;
            _activePressureBuffer = 0;
            _pressureFieldEntity = Entity.Null;
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
            if (config.BackpressureThreshold <= 0f)
            {
                config.BackpressureThreshold = 4.0f;
                changedDefaults = true;
            }

            if (config.BackpressureK <= 0f)
            {
                config.BackpressureK = 0.40f;
                changedDefaults = true;
            }

            if (config.MinSpeedFactor <= 0f)
            {
                config.MinSpeedFactor = 0.30f;
                changedDefaults = true;
            }

            if (config.BackpressureMaxFactor <= 0f)
            {
                config.BackpressureMaxFactor = 1.0f;
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
            int fieldInterval = math.clamp(config.FieldUpdateIntervalFrames, 1, 8);
            int blurPasses = math.clamp(config.BlurPasses, 0, 2);
            bool shouldRebuild = resized || ((_frameIndex % fieldInterval) == 0);
            _pressureLookup.Update(ref state);

            JobHandle dependency = state.Dependency;
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
                Pressure = activePressure,
                PressureStrength = pressureStrength,
                MaxPush = maxPushThisFrame,
                SpeedFractionCap = speedFractionCap,
                BlockedPenalty = blockedPenalty,
                CenterWorld = mapData.CenterWorld,
                DeltaTime = deltaTime
            };
            state.Dependency = applyPressureJob.ScheduleParallel(dependency);
            _frameIndex++;
        }

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
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            [ReadOnly] public NativeArray<float> Pressure;
            public float PressureStrength;
            public float MaxPush;
            public float SpeedFractionCap;
            public float BlockedPenalty;
            public float2 CenterWorld;
            public float DeltaTime;

            private void Execute([EntityIndexInQuery] int entityIndex, ref LocalTransform transform, in ZombieTag tag, in ZombieMoveSpeed moveSpeed)
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

                float localPressure = Pressure[index];
                if (localPressure <= Epsilon)
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
                        float speedBudget = moveStep * SpeedFractionCap;
                        float effectiveCap = math.min(MaxPush, speedBudget);
                        if (effectiveCap > 0f)
                        {
                            float push = math.min(rawPush, effectiveCap);
                            if (push > 0f)
                            {
                                pressureDelta = direction * push;
                            }
                        }
                    }
                }

                float2 candidate = position + pressureDelta;
                if (math.lengthsq(candidate - position) <= Epsilon)
                {
                    return;
                }

                if (!IsWalkableWorld(candidate, ref flow))
                {
                    return;
                }

                transform.Position = new float3(candidate.x, candidate.y, transform.Position.z);
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

                // If all immediate neighbors are similarly dense, push away from blocked pressure.
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

                // Remove only the backwards component along flow to avoid pressure driving away from the goal.
                return direction - (flowN * flowDot);
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
