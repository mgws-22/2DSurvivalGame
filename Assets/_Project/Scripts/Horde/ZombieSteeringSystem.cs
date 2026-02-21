using Project.Map;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ZombieSteeringSystem : ISystem
    {
        private const byte NoneDirection = 255;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<FlowFieldSingleton>();
            state.RequireForUpdate<ZombieTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            MapRuntimeData mapData = SystemAPI.GetSingleton<MapRuntimeData>();
            FlowFieldSingleton flowSingleton = SystemAPI.GetSingleton<FlowFieldSingleton>();
            if (!flowSingleton.Blob.IsCreated)
            {
                return;
            }

            ZombieSteeringJob job = new ZombieSteeringJob
            {
                DeltaTime = deltaTime,
                MapData = mapData,
                Flow = flowSingleton.Blob
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct ZombieSteeringJob : IJobEntity
        {
            public float DeltaTime;
            public MapRuntimeData MapData;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;

            private void Execute(ref ZombieSteeringState steeringState, ref ZombieGoalIntent goalIntent, in LocalTransform transform, in ZombieMoveSpeed moveSpeed, in ZombieTag tag)
            {
                goalIntent.Direction = float2.zero;
                goalIntent.StepDistance = 0f;

                float2 position = transform.Position.xy;
                float stepDistance = moveSpeed.Value * DeltaTime;
                if (stepDistance <= 0f)
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                float2 desiredDirection = ResolveDesiredDirection(position);
                if (math.lengthsq(desiredDirection) < 0.0001f)
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                float2 nextPosition = position + (desiredDirection * stepDistance);
                if (!IsWorldPositionWalkable(nextPosition))
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                goalIntent.Direction = desiredDirection;
                goalIntent.StepDistance = stepDistance;
            }

            private float2 ResolveDesiredDirection(float2 position)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                int2 grid = WorldToFlowGrid(position, ref flow);
                if (!IsInFlowBounds(grid, ref flow))
                {
                    return NormalizeFast(MapData.CenterWorld - position);
                }

                int flowIndex = grid.x + (grid.y * flow.Width);
                if (flowIndex < 0 || flowIndex >= flow.Dir.Length)
                {
                    return NormalizeFast(MapData.CenterWorld - position);
                }

                byte dir = flow.Dir[flowIndex];
                if (dir == NoneDirection)
                {
                    return NormalizeFast(MapData.CenterWorld - position);
                }

                return GetDirectionFromByte(dir);
            }

            private bool IsWorldPositionWalkable(float2 worldPosition)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                int2 grid = WorldToFlowGrid(worldPosition, ref flow);
                if (!IsInFlowBounds(grid, ref flow))
                {
                    return true;
                }

                int index = grid.x + (grid.y * flow.Width);
                if (index < 0 || index >= flow.Dir.Length)
                {
                    return false;
                }

                return flow.Dist[index] != ushort.MaxValue;
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

            private static float2 NormalizeFast(float2 v)
            {
                float lenSq = math.lengthsq(v);
                if (lenSq <= 0.000001f)
                {
                    return float2.zero;
                }

                return v * math.rsqrt(lenSq);
            }

            private float2 GetDirectionFromByte(byte dir)
            {
                if (dir >= Flow.Value.DirLut.Length)
                {
                    return float2.zero;
                }

                return Flow.Value.DirLut[dir];
            }
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(HordePressureFieldSystem))]
    [UpdateBefore(typeof(HordeSeparationSystem))]
    [UpdateBefore(typeof(HordeHardSeparationSystem))]
    [UpdateBefore(typeof(WallRepulsionSystem))]
    public partial struct HordeBackpressureSystem : ISystem
    {
        private BufferLookup<PressureCell> _pressureLookup;
        private EntityQuery _pressureBufferQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FlowFieldSingleton>();
            state.RequireForUpdate<ZombieTag>();
            state.RequireForUpdate<HordePressureConfig>();
            _pressureLookup = state.GetBufferLookup<PressureCell>(true);
            _pressureBufferQuery = state.GetEntityQuery(ComponentType.ReadOnly<PressureFieldBufferTag>());
        }

        [BurstCompile]
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

            HordePressureConfig pressureConfig = SystemAPI.GetSingleton<HordePressureConfig>();
            float backpressureThreshold = math.max(0f, pressureConfig.BackpressureThreshold);
            float backpressureK = math.max(0f, pressureConfig.BackpressureK);
            float minSpeedFactor = math.clamp(pressureConfig.MinSpeedFactor, 0f, 1f);
            float maxSpeedFactor = math.clamp(pressureConfig.BackpressureMaxFactor, 0f, 1f);
            if (maxSpeedFactor < minSpeedFactor)
            {
                maxSpeedFactor = minSpeedFactor;
            }

            _pressureLookup.Update(ref state);
            Entity pressureFieldEntity = _pressureBufferQuery.IsEmptyIgnoreFilter
                ? Entity.Null
                : _pressureBufferQuery.GetSingletonEntity();

            ApplyGoalIntentWithBackpressureJob job = new ApplyGoalIntentWithBackpressureJob
            {
                Flow = flowSingleton.Blob,
                PressureLookup = _pressureLookup,
                PressureFieldEntity = pressureFieldEntity,
                HasPressureFieldEntity = pressureFieldEntity != Entity.Null ? (byte)1 : (byte)0,
                PressureEnabled = pressureConfig.Enabled,
                BackpressureThreshold = backpressureThreshold,
                BackpressureK = backpressureK,
                MinSpeedFactor = minSpeedFactor,
                MaxSpeedFactor = maxSpeedFactor
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct ApplyGoalIntentWithBackpressureJob : IJobEntity
        {
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;
            [ReadOnly] public BufferLookup<PressureCell> PressureLookup;
            public Entity PressureFieldEntity;
            public byte HasPressureFieldEntity;
            public byte PressureEnabled;
            public float BackpressureThreshold;
            public float BackpressureK;
            public float MinSpeedFactor;
            public float MaxSpeedFactor;

            private void Execute(ref LocalTransform transform, ref ZombieSteeringState steeringState, in ZombieGoalIntent goalIntent, in ZombieTag tag)
            {
                float2 desiredDirection = goalIntent.Direction;
                float directionLenSq = math.lengthsq(desiredDirection);
                if (goalIntent.StepDistance <= 0f || directionLenSq < 0.0001f)
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                desiredDirection *= math.rsqrt(directionLenSq);
                float2 position = transform.Position.xy;
                float speedScale = ResolveBackpressureSpeedScale(position);
                float stepDistance = goalIntent.StepDistance * speedScale;
                if (stepDistance <= 0f)
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                float2 nextPosition = position + (desiredDirection * stepDistance);
                if (!IsWorldPositionWalkable(nextPosition))
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                transform.Position = new float3(nextPosition.x, nextPosition.y, transform.Position.z);
                steeringState.LastDirection = desiredDirection;
            }

            private float ResolveBackpressureSpeedScale(float2 position)
            {
                if (PressureEnabled == 0 || HasPressureFieldEntity == 0)
                {
                    return 1f;
                }

                if (!PressureLookup.HasBuffer(PressureFieldEntity))
                {
                    return 1f;
                }

                ref FlowFieldBlob flow = ref Flow.Value;
                int2 grid = WorldToFlowGrid(position, ref flow);
                if (!IsInFlowBounds(grid, ref flow))
                {
                    return MaxSpeedFactor;
                }

                int flowIndex = grid.x + (grid.y * flow.Width);
                DynamicBuffer<PressureCell> pressureBuffer = PressureLookup[PressureFieldEntity];
                if (flowIndex < 0 || flowIndex >= pressureBuffer.Length)
                {
                    return MaxSpeedFactor;
                }

                float localPressure = pressureBuffer[flowIndex].Value;
                float excess = math.max(0f, localPressure - BackpressureThreshold);
                float rawScale = 1f / (1f + (BackpressureK * excess));
                return math.clamp(rawScale, MinSpeedFactor, MaxSpeedFactor);
            }

            private bool IsWorldPositionWalkable(float2 worldPosition)
            {
                ref FlowFieldBlob flow = ref Flow.Value;
                int2 grid = WorldToFlowGrid(worldPosition, ref flow);
                if (!IsInFlowBounds(grid, ref flow))
                {
                    return true;
                }

                int index = grid.x + (grid.y * flow.Width);
                if (index < 0 || index >= flow.Dir.Length)
                {
                    return false;
                }

                return flow.Dist[index] != ushort.MaxValue;
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
}
