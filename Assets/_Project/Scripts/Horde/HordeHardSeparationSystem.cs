using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
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
        private static bool s_loggedRunning;
        private static readonly ProfilerMarker BuildGridMarker = new ProfilerMarker("HordeHardSeparation.BuildGrid");
        private static readonly ProfilerMarker IterationComputeMarker = new ProfilerMarker("HordeHardSeparation.IterationCompute");
        private static readonly ProfilerMarker IterationApplyMarker = new ProfilerMarker("HordeHardSeparation.IterationApply");
        private static readonly ProfilerMarker WriteBackMarker = new ProfilerMarker("HordeHardSeparation.WriteBack");

        private EntityQuery _zombieQuery;
        private NativeList<float3> _positionsA;
        private NativeList<float3> _positionsB;
        private NativeList<float3> _deltas;
        private NativeParallelMultiHashMap<int, int> _gridA;
        private NativeParallelMultiHashMap<int, int> _gridB;

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ZombieTag>(),
                ComponentType.ReadWrite<LocalTransform>());

            _positionsA = new NativeList<float3>(1024, Allocator.Persistent);
            _positionsB = new NativeList<float3>(1024, Allocator.Persistent);
            _deltas = new NativeList<float3>(1024, Allocator.Persistent);
            _gridA = new NativeParallelMultiHashMap<int, int>(2048, Allocator.Persistent);
            _gridB = new NativeParallelMultiHashMap<int, int>(2048, Allocator.Persistent);

            state.RequireForUpdate(_zombieQuery);
        }

        public void OnDestroy(ref SystemState state)
        {
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
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton(out HordeHardSeparationConfig config) || config.Enabled == 0)
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

            config = SanitizeConfig(config);
            float minDist = config.Radius * 2f;
            float minDistSq = minDist * minDist;
            float invCellSize = 1f / config.CellSize;

            EnsureCapacity(count);
            _positionsA.ResizeUninitialized(count);
            _positionsB.ResizeUninitialized(count);
            _deltas.ResizeUninitialized(count);
            _gridA.Clear();
            _gridB.Clear();

            GatherPositionsJob gatherJob = new GatherPositionsJob
            {
                Positions = _positionsA.AsArray()
            };
            state.Dependency = gatherJob.ScheduleParallel(_zombieQuery, state.Dependency);

            NativeArray<float3> posRead = _positionsA.AsArray();
            NativeArray<float3> posWrite = _positionsB.AsArray();
            NativeArray<float3> deltas = _deltas.AsArray();

            for (int iteration = 0; iteration < config.Iterations; iteration++)
            {
                NativeParallelMultiHashMap<int, int> grid = (iteration & 1) == 0 ? _gridA : _gridB;

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
                        InvCellSize = invCellSize,
                        MinDist = minDist,
                        MinDistSq = minDistSq,
                        MaxNeighbors = config.MaxNeighbors,
                        MaxCorrectionPerIter = config.MaxCorrectionPerIter,
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

            using (WriteBackMarker.Auto())
            {
                WriteBackJob writeBackJob = new WriteBackJob
                {
                    Positions = posRead
                };
                state.Dependency = writeBackJob.ScheduleParallel(_zombieQuery, state.Dependency);
            }
        }

        private void EnsureCapacity(int count)
        {
            int target = math.ceilpow2(count);
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

            if (_gridA.Capacity < target)
            {
                _gridA.Capacity = target;
            }

            if (_gridB.Capacity < target)
            {
                _gridB.Capacity = target;
            }
        }

        private static HordeHardSeparationConfig SanitizeConfig(HordeHardSeparationConfig config)
        {
            config.Radius = math.max(0.001f, config.Radius);
            config.CellSize = math.max(0.001f, config.CellSize);
            config.MaxNeighbors = math.clamp(config.MaxNeighbors, 1, 32);
            config.Iterations = math.clamp(config.Iterations, 1, 2);
            config.MaxCorrectionPerIter = math.max(0f, config.MaxCorrectionPerIter);
            config.Slop = math.max(0f, config.Slop);
            return config;
        }

        [BurstCompile]
        private partial struct GatherPositionsJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public NativeArray<float3> Positions;

            private void Execute([EntityIndexInQuery] int index, in ZombieTag tag, in LocalTransform transform)
            {
                Positions[index] = transform.Position;
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
            [NativeDisableParallelForRestriction] public NativeArray<float3> Delta;
            public float InvCellSize;
            public float MinDist;
            public float MinDistSq;
            public int MaxNeighbors;
            public float MaxCorrectionPerIter;
            public float Slop;

            public void Execute(int index)
            {
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
