using Project.Map;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ZombieSpawnSystem : ISystem
    {
        private EntityQuery _zombieQuery;
        private EntityQuery _spawnStateQuery;

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(ComponentType.ReadOnly<ZombieTag>());
            _spawnStateQuery = state.GetEntityQuery(ComponentType.ReadWrite<ZombieSpawnState>());
            state.RequireForUpdate<ZombieSpawnConfig>();
            state.RequireForUpdate<MapRuntimeData>();
        }

        public void OnUpdate(ref SystemState state)
        {
            ZombieSpawnConfig config = SystemAPI.GetSingleton<ZombieSpawnConfig>();
            if (config.Prefab == Entity.Null || config.SpawnRate <= 0f || config.SpawnBatchSize <= 0 || config.MaxAlive <= 0)
            {
                return;
            }

            EntityManager entityManager = state.EntityManager;
            Entity spawnStateEntity;
            ZombieSpawnState stateData;

            if (_spawnStateQuery.IsEmptyIgnoreFilter)
            {
                spawnStateEntity = entityManager.CreateEntity(typeof(ZombieSpawnState));
                stateData = new ZombieSpawnState
                {
                    Random = CreateRandom(config.Seed),
                    LastSeed = config.Seed,
                    SpawnAccumulator = 0f
                };
                entityManager.SetComponentData(spawnStateEntity, stateData);
            }
            else
            {
                spawnStateEntity = _spawnStateQuery.GetSingletonEntity();
                stateData = entityManager.GetComponentData<ZombieSpawnState>(spawnStateEntity);
                if (stateData.LastSeed != config.Seed)
                {
                    stateData.Random = CreateRandom(config.Seed);
                    stateData.LastSeed = config.Seed;
                    stateData.SpawnAccumulator = 0f;
                    entityManager.SetComponentData(spawnStateEntity, stateData);
                }
            }

            int aliveCount = _zombieQuery.CalculateEntityCount();
            if (aliveCount >= config.MaxAlive)
            {
                return;
            }

            stateData.SpawnAccumulator += SystemAPI.Time.DeltaTime * config.SpawnRate;
            int waveCount = (int)math.floor(stateData.SpawnAccumulator);
            if (waveCount <= 0)
            {
                entityManager.SetComponentData(spawnStateEntity, stateData);
                return;
            }

            int spawnCount = waveCount * config.SpawnBatchSize;
            stateData.SpawnAccumulator -= waveCount;

            int available = config.MaxAlive - aliveCount;
            spawnCount = math.min(spawnCount, available);
            if (spawnCount <= 0)
            {
                entityManager.SetComponentData(spawnStateEntity, stateData);
                return;
            }

            MapRuntimeData mapData = SystemAPI.GetSingleton<MapRuntimeData>();

            EntityCommandBuffer ecb = SystemAPI
                .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            Unity.Mathematics.Random random = stateData.Random;
            for (int i = 0; i < spawnCount; i++)
            {
                int2 spawnCell = SampleSpawnRingCell(ref random, mapData.Width, mapData.Height, mapData.SpawnMargin);
                Entity entity = ecb.Instantiate(config.Prefab);

                float3 position = mapData.GridToWorld(spawnCell, 0f);
                LocalTransform transform = LocalTransform.FromPositionRotationScale(position, quaternion.identity, 1f);
                ecb.SetComponent(entity, transform);
            }

            stateData.Random = random;
            entityManager.SetComponentData(spawnStateEntity, stateData);
        }

        private static Unity.Mathematics.Random CreateRandom(uint seed)
        {
            uint hashed = math.hash(new uint2(seed, 0xB5297A4Du));
            if (hashed == 0u)
            {
                hashed = 1u;
            }

            return new Unity.Mathematics.Random(hashed);
        }

        private static int2 SampleSpawnRingCell(ref Unity.Mathematics.Random random, int width, int height, int spawnMargin)
        {
            int margin = math.max(0, spawnMargin);
            int fullWidth = width + (margin * 2);

            int topArea = fullWidth * margin;
            int bottomArea = topArea;
            int leftArea = margin * height;
            int rightArea = leftArea;
            int totalArea = topArea + bottomArea + leftArea + rightArea;

            if (totalArea <= 0)
            {
                int side = random.NextInt(4);
                return side switch
                {
                    0 => new int2(random.NextInt(0, width), height),
                    1 => new int2(random.NextInt(0, width), -1),
                    2 => new int2(-1, random.NextInt(0, height)),
                    _ => new int2(width, random.NextInt(0, height))
                };
            }

            int pick = random.NextInt(totalArea);

            if (pick < topArea)
            {
                return new int2(random.NextInt(-margin, width + margin), random.NextInt(height, height + margin));
            }

            pick -= topArea;
            if (pick < bottomArea)
            {
                return new int2(random.NextInt(-margin, width + margin), random.NextInt(-margin, 0));
            }

            pick -= bottomArea;
            if (pick < leftArea)
            {
                return new int2(random.NextInt(-margin, 0), random.NextInt(0, height));
            }

            return new int2(random.NextInt(width, width + margin), random.NextInt(0, height));
        }
    }
}
