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
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MapRuntimeData>();
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
            DynamicBuffer<MapWalkableCell> walkableBuffer = SystemAPI.GetSingletonBuffer<MapWalkableCell>(true);

            ZombieSteeringJob job = new ZombieSteeringJob
            {
                DeltaTime = deltaTime,
                MapData = mapData,
                Walkable = walkableBuffer.AsNativeArray()
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct ZombieSteeringJob : IJobEntity
        {
            public float DeltaTime;
            public MapRuntimeData MapData;

            [ReadOnly] public NativeArray<MapWalkableCell> Walkable;

            private void Execute(ref LocalTransform transform, ref ZombieSteeringState steeringState, in ZombieMoveSpeed moveSpeed, in ZombieTag tag)
            {
                float2 position = transform.Position.xy;
                float2 toCenter = MapData.CenterWorld - position;
                float currentDistanceSq = math.lengthsq(toCenter);
                if (currentDistanceSq <= 0.0001f)
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                float stepDistance = moveSpeed.Value * DeltaTime;
                if (stepDistance <= 0f)
                {
                    return;
                }

                float2 desiredDirection = math.normalizesafe(toCenter);
                float2 bestDirection = float2.zero;
                float bestDistanceSq = currentDistanceSq;
                bool found = false;

                TryDirection(
                    desiredDirection,
                    stepDistance,
                    position,
                    ref bestDirection,
                    ref bestDistanceSq,
                    ref found);

                for (int i = 0; i < 8; i++)
                {
                    float2 dir = GetDirectionFromIndex(i);
                    TryDirection(
                        dir,
                        stepDistance,
                        position,
                        ref bestDirection,
                        ref bestDistanceSq,
                        ref found);
                }

                if (!found)
                {
                    steeringState.LastDirection = float2.zero;
                    return;
                }

                float2 nextPosition = position + (bestDirection * stepDistance);
                transform.Position = new float3(nextPosition.x, nextPosition.y, transform.Position.z);
                steeringState.LastDirection = bestDirection;
            }

            private void TryDirection(
                float2 direction,
                float stepDistance,
                float2 currentPosition,
                ref float2 bestDirection,
                ref float bestDistanceSq,
                ref bool found)
            {
                if (math.lengthsq(direction) < 0.0001f)
                {
                    return;
                }

                float2 nextPosition = currentPosition + (direction * stepDistance);
                if (!IsWorldPositionWalkable(nextPosition))
                {
                    return;
                }

                float distanceSq = math.lengthsq(MapData.CenterWorld - nextPosition);
                if (distanceSq + 0.00001f >= bestDistanceSq)
                {
                    return;
                }

                bestDistanceSq = distanceSq;
                bestDirection = direction;
                found = true;
            }

            private bool IsWorldPositionWalkable(float2 worldPosition)
            {
                int2 grid = MapData.WorldToGrid(worldPosition);
                if (!MapData.IsInMap(grid))
                {
                    return true;
                }

                int index = MapData.ToIndex(grid);
                if (index < 0 || index >= Walkable.Length)
                {
                    return false;
                }

                return Walkable[index].IsWalkable;
            }

            private static float2 GetDirectionFromIndex(int index)
            {
                const float diagonal = 0.70710677f;

                return index switch
                {
                    0 => new float2(0f, 1f),
                    1 => new float2(diagonal, diagonal),
                    2 => new float2(1f, 0f),
                    3 => new float2(diagonal, -diagonal),
                    4 => new float2(0f, -1f),
                    5 => new float2(-diagonal, -diagonal),
                    6 => new float2(-1f, 0f),
                    _ => new float2(-diagonal, diagonal)
                };
            }
        }
    }
}
