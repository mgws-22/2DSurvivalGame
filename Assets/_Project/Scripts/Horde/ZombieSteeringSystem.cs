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
        private static bool s_loggedAccelerationConfigOnce;
        private BufferLookup<PressureCell> _pressureLookup;
        private EntityQuery _pressureBufferQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<FlowFieldSingleton>();
            state.RequireForUpdate<ZombieTag>();
            state.RequireForUpdate<HordePressureConfig>();

            EntityQuery accelConfigQuery = state.GetEntityQuery(ComponentType.ReadWrite<ZombieAccelerationConfig>());
            Entity accelConfigEntity;
            if (accelConfigQuery.IsEmptyIgnoreFilter)
            {
                accelConfigEntity = state.EntityManager.CreateEntity(typeof(ZombieAccelerationConfig));
                state.EntityManager.SetComponentData(accelConfigEntity, new ZombieAccelerationConfig
                {
                    Enabled = 1,
                    TimeToMaxSpeedSeconds = 0.35f,
                    MaxAccel = 1f,
                    DecelMultiplier = 2f
                });
            }
            else
            {
                accelConfigEntity = accelConfigQuery.GetSingletonEntity();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (state.EntityManager.Exists(accelConfigEntity))
            {
                state.EntityManager.SetName(accelConfigEntity, "ZombieAccelerationConfig");
                if (!s_loggedAccelerationConfigOnce)
                {
                    ZombieAccelerationConfig cfg = state.EntityManager.GetComponentData<ZombieAccelerationConfig>(accelConfigEntity);
                    UnityEngine.Debug.Log(
                        $"[HordeAccel] cfg enabled={cfg.Enabled} timeToMax={cfg.TimeToMaxSpeedSeconds:F3} maxAccel={cfg.MaxAccel:F3} decelMultiplier={cfg.DecelMultiplier:F2}");
                    s_loggedAccelerationConfigOnce = true;
                }
            }
#endif

            state.RequireForUpdate<ZombieAccelerationConfig>();
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
            ZombieAccelerationConfig accelerationConfig = SystemAPI.GetSingleton<ZombieAccelerationConfig>();
            float backpressureThreshold = math.max(0f, pressureConfig.BackpressureThreshold);
            float backpressureK = math.max(0f, pressureConfig.BackpressureK);
            float minSpeedFactor = math.clamp(pressureConfig.MinSpeedFactor, 0f, 1f);
            float maxSpeedFactor = math.clamp(pressureConfig.BackpressureMaxFactor, 0f, 1f);
            float timeToMaxSpeedSeconds = math.max(0f, accelerationConfig.TimeToMaxSpeedSeconds);
            float maxAccel = math.max(0f, accelerationConfig.MaxAccel);
            float decelMultiplier = math.max(1f, accelerationConfig.DecelMultiplier);
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
                MaxSpeedFactor = maxSpeedFactor,
                DeltaTime = deltaTime,
                AccelerationEnabled = accelerationConfig.Enabled,
                TimeToMaxSpeedSeconds = timeToMaxSpeedSeconds,
                MaxAccel = maxAccel,
                DecelMultiplier = decelMultiplier
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
            public float DeltaTime;
            public byte AccelerationEnabled;
            public float TimeToMaxSpeedSeconds;
            public float MaxAccel;
            public float DecelMultiplier;

            private void Execute(
                ref LocalTransform transform,
                ref ZombieSteeringState steeringState,
                ref ZombieVelocity velocity,
                in ZombieGoalIntent goalIntent,
                in ZombieMoveSpeed moveSpeed,
                in ZombieTag tag)
            {
                float2 desiredDirection = goalIntent.Direction;
                float desiredSpeed = 0f;
                float2 position = transform.Position.xy;

                float directionLenSq = math.lengthsq(desiredDirection);
                if (goalIntent.StepDistance > 0f && directionLenSq >= 0.0001f)
                {
                    desiredDirection *= math.rsqrt(directionLenSq);
                    float speedScale = ResolveBackpressureSpeedScale(position);
                    desiredSpeed = (goalIntent.StepDistance * speedScale) / math.max(1e-5f, DeltaTime);
                }
                else
                {
                    desiredDirection = float2.zero;
                }

                float2 desiredVelocity = desiredDirection * desiredSpeed;
                float2 nextVelocity = ResolveNextVelocity(velocity.Value, desiredVelocity, moveSpeed.Value);
                float nextVelocityLenSq = math.lengthsq(nextVelocity);
                if (nextVelocityLenSq < 0.000001f)
                {
                    nextVelocity = float2.zero;
                    nextVelocityLenSq = 0f;
                }

                float2 nextPosition = position + (nextVelocity * DeltaTime);
                if (nextVelocityLenSq > 0f && !IsWorldPositionWalkable(nextPosition))
                {
                    velocity.Value = float2.zero;
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                velocity.Value = nextVelocity;
                transform.Position = new float3(nextPosition.x, nextPosition.y, transform.Position.z);

                if (nextVelocityLenSq > 0f)
                {
                    steeringState.LastDirection = nextVelocity * math.rsqrt(nextVelocityLenSq);
                }
                else
                {
                    steeringState.LastDirection = float2.zero;
                }
            }

            private float2 ResolveNextVelocity(float2 currentVelocity, float2 desiredVelocity, float moveSpeed)
            {
                if (AccelerationEnabled == 0)
                {
                    return desiredVelocity;
                }

                float maxAccel = ResolveMaxAccel(moveSpeed);
                if (maxAccel <= 0f)
                {
                    return desiredVelocity;
                }

                float currentSpeed = math.length(currentVelocity);
                float desiredSpeed = math.length(desiredVelocity);
                bool slowing = desiredSpeed < currentSpeed;
                float accelLimit = slowing ? (maxAccel * DecelMultiplier) : maxAccel;
                float maxDv = accelLimit * DeltaTime;
                if (maxDv <= 0f)
                {
                    return currentVelocity;
                }

                float2 dv = desiredVelocity - currentVelocity;
                float maxDvSq = maxDv * maxDv;
                float dvLenSq = math.lengthsq(dv);
                if (dvLenSq > maxDvSq)
                {
                    dv *= maxDv * math.rsqrt(dvLenSq);
                }

                return currentVelocity + dv;
            }

            private float ResolveMaxAccel(float moveSpeed)
            {
                if (MaxAccel > 0f)
                {
                    return MaxAccel;
                }

                if (TimeToMaxSpeedSeconds > 0.0001f)
                {
                    return math.max(0f, moveSpeed) / TimeToMaxSpeedSeconds;
                }

                return 0f;
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
