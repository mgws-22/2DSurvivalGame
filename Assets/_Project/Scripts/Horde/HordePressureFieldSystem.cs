using Project.Map;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ZombieSteeringSystem))]
    [UpdateBefore(typeof(HordeSeparationSystem))]
    [UpdateBefore(typeof(HordeHardSeparationSystem))]
    public partial struct HordePressureFieldSystem : ISystem
    {
        private const float Epsilon = 1e-6f;
        private const float Diagonal = 0.70710677f;

        private NativeArray<int> _density;
        private NativeArray<float> _pressureA;
        private NativeArray<float> _pressureB;
        private int _cellCount;
        private int _frameIndex;
        private byte _activePressureBuffer;

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
                    TargetUnitsPerCell = 2.5f,
                    PressureStrength = 3f,
                    MaxPushPerFrame = 0.4f,
                    SpeedFractionCap = 0.4f,
                    BlockedCellPenalty = 6f,
                    FieldUpdateIntervalFrames = 1,
                    BlurPasses = 1,
                    DisablePairwiseSeparationWhenPressureEnabled = 0
                });
            }

            state.RequireForUpdate<HordePressureConfig>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_density.IsCreated)
            {
                _density.Dispose();
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
            _frameIndex = 0;
            _activePressureBuffer = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            FlowFieldSingleton flowSingleton = SystemAPI.GetSingleton<FlowFieldSingleton>();
            if (!flowSingleton.Blob.IsCreated)
            {
                return;
            }

            MapRuntimeData mapData = SystemAPI.GetSingleton<MapRuntimeData>();

            HordePressureConfig config = SystemAPI.GetSingleton<HordePressureConfig>();
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
            if (!_density.IsCreated || !_pressureA.IsCreated || !_pressureB.IsCreated)
            {
                _frameIndex++;
                return;
            }

            float targetUnitsPerCell = math.max(0f, config.TargetUnitsPerCell);
            float pressureStrength = math.max(0f, config.PressureStrength);
            float maxPush = math.max(0f, config.MaxPushPerFrame);
            float speedFractionCap = math.clamp(config.SpeedFractionCap, 0f, 1f);
            float blockedPenalty = math.max(0f, config.BlockedCellPenalty);
            int fieldInterval = math.clamp(config.FieldUpdateIntervalFrames, 1, 8);
            int blurPasses = math.clamp(config.BlurPasses, 0, 2);
            bool shouldRebuild = resized || ((_frameIndex % fieldInterval) == 0);

            JobHandle dependency = state.Dependency;
            NativeArray<float> activePressure = _activePressureBuffer == 0 ? _pressureA : _pressureB;

            if (shouldRebuild)
            {
                ClearIntArrayJob clearDensityJob = new ClearIntArrayJob
                {
                    Values = _density
                };
                dependency = clearDensityJob.Schedule(cellCount, 256, dependency);

                AccumulateDensityJob accumulateDensityJob = new AccumulateDensityJob
                {
                    Density = _density,
                    Flow = flowSingleton.Blob
                };
                dependency = accumulateDensityJob.Schedule(dependency);

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

            ApplyPressureJob applyPressureJob = new ApplyPressureJob
            {
                Flow = flowSingleton.Blob,
                Pressure = activePressure,
                PressureStrength = pressureStrength,
                MaxPush = maxPush,
                SpeedFractionCap = speedFractionCap,
                BlockedPenalty = blockedPenalty,
                CenterWorld = mapData.CenterWorld,
                DeltaTime = SystemAPI.Time.DeltaTime
            };
            state.Dependency = applyPressureJob.ScheduleParallel(dependency);
            _frameIndex++;
        }

        private bool EnsureFieldSize(int cellCount)
        {
            if (_cellCount == cellCount && _density.IsCreated && _pressureA.IsCreated && _pressureB.IsCreated)
            {
                return false;
            }

            if (_density.IsCreated)
            {
                _density.Dispose();
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
            public NativeArray<int> Density;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;

            private void Execute(in LocalTransform transform, in ZombieTag tag)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                int2 cell = WorldToFlowGrid(transform.Position.xy, ref flow);
                if (!IsInFlowBounds(cell, ref flow))
                {
                    return;
                }

                int index = cell.x + (cell.y * flow.Width);
                if (index < 0 || index >= Density.Length)
                {
                    return;
                }

                if (flow.Dist[index] == ushort.MaxValue)
                {
                    return;
                }

                Density[index] = Density[index] + 1;
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

                float2 direction = ResolvePressureDirection(cell, position, entityIndex, localPressure, ref flow);
                float dirLenSq = math.lengthsq(direction);
                if (dirLenSq <= Epsilon)
                {
                    return;
                }

                float2 flowDirection = ResolveFlowDirection(index, position, ref flow);
                direction = ConstrainAgainstFlow(direction, flowDirection, entityIndex);
                dirLenSq = math.lengthsq(direction);
                if (dirLenSq <= Epsilon)
                {
                    return;
                }

                direction *= math.rsqrt(dirLenSq);
                float rawPush = localPressure * PressureStrength * DeltaTime;
                float speedBudget = math.max(0f, moveSpeed.Value) * DeltaTime * SpeedFractionCap;
                float effectiveCap = math.min(MaxPush, speedBudget);
                if (effectiveCap <= 0f)
                {
                    return;
                }

                float push = math.min(rawPush, effectiveCap);
                if (push <= 0f)
                {
                    return;
                }

                float2 candidate = position + (direction * push);
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

            private static float2 ConstrainAgainstFlow(float2 direction, float2 flowDirection, int entityIndex)
            {
                float flowLenSq = math.lengthsq(flowDirection);
                if (flowLenSq <= Epsilon)
                {
                    return direction;
                }

                float2 flowN = flowDirection * math.rsqrt(flowLenSq);
                float flowDot = math.dot(direction, flowN);
                if (flowDot >= -0.15f)
                {
                    return direction;
                }

                float2 constrained = direction - (flowN * flowDot);
                float constrainedLenSq = math.lengthsq(constrained);
                if (constrainedLenSq <= Epsilon)
                {
                    float2 lateral = new float2(-flowN.y, flowN.x);
                    uint h = (uint)entityIndex * 0x9E3779B9u;
                    if ((h & 1u) == 0u)
                    {
                        lateral = -lateral;
                    }

                    return lateral;
                }

                return constrained;
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
