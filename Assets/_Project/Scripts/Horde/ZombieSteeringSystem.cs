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
            state.RequireForUpdate<GatePoint>();
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
            DynamicBuffer<GatePoint> gates = SystemAPI.GetSingletonBuffer<GatePoint>(true);
            FlowFieldSingleton flowSingleton = SystemAPI.GetSingleton<FlowFieldSingleton>();
            if (!flowSingleton.Blob.IsCreated)
            {
                return;
            }

            ZombieSteeringJob job = new ZombieSteeringJob
            {
                DeltaTime = deltaTime,
                MapData = mapData,
                Gates = gates.AsNativeArray(),
                Flow = flowSingleton.Blob
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct ZombieSteeringJob : IJobEntity
        {
            public float DeltaTime;
            public MapRuntimeData MapData;
            [ReadOnly] public NativeArray<GatePoint> Gates;
            [ReadOnly] public BlobAssetReference<FlowFieldBlob> Flow;

            private void Execute(ref LocalTransform transform, ref ZombieSteeringState steeringState, in ZombieMoveSpeed moveSpeed, in ZombieTag tag)
            {
                float2 position = transform.Position.xy;
                float stepDistance = moveSpeed.Value * DeltaTime;
                if (stepDistance <= 0f)
                {
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

                transform.Position = new float3(nextPosition.x, nextPosition.y, transform.Position.z);
                steeringState.LastDirection = desiredDirection;
            }

            private float2 ResolveDesiredDirection(float2 position)
            {
                int2 grid = MapData.WorldToGrid(position);
                if (!MapData.IsInMap(grid))
                {
                    return DirectionToNearestGate(position);
                }

                int flowIndex = MapData.ToIndex(grid);
                ref FlowFieldBlob flow = ref Flow.Value;
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
                int2 grid = MapData.WorldToGrid(worldPosition);
                if (!MapData.IsInMap(grid))
                {
                    return true;
                }

                int index = MapData.ToIndex(grid);
                ref FlowFieldBlob flow = ref Flow.Value;
                if (index < 0 || index >= flow.Dir.Length)
                {
                    return false;
                }

                return flow.Dir[index] != NoneDirection || flow.Dist[index] == 0;
            }

            private float2 DirectionToNearestGate(float2 position)
            {
                float2 bestDelta = MapData.CenterWorld - position;
                float bestLenSq = math.lengthsq(bestDelta);

                for (int i = 0; i < Gates.Length; i++)
                {
                    float2 delta = Gates[i].WorldPos - position;
                    float lenSq = math.lengthsq(delta);
                    if (lenSq < bestLenSq)
                    {
                        bestLenSq = lenSq;
                        bestDelta = delta;
                    }
                }

                return NormalizeFast(bestDelta);
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
}
