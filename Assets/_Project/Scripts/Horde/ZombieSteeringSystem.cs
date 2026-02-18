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
}
