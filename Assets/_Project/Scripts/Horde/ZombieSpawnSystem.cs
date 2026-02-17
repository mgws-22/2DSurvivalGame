using Project.Map;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
#endif

namespace Project.Horde
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ZombieSpawnSystem : ISystem
    {
        private EntityQuery _zombieQuery;
        private EntityQuery _spawnStateQuery;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static bool s_loggedSpawnDiagnostics;
        private static bool s_loggedFirstSpawnBatch;
#endif

        public void OnCreate(ref SystemState state)
        {
            _zombieQuery = state.GetEntityQuery(ComponentType.ReadOnly<ZombieTag>());
            _spawnStateQuery = state.GetEntityQuery(ComponentType.ReadWrite<ZombieSpawnState>());
        }

        public void OnUpdate(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;

            bool hasConfig = SystemAPI.TryGetSingleton(out ZombieSpawnConfig config);
            bool hasMap = SystemAPI.TryGetSingleton(out MapRuntimeData mapData);
            int aliveCountBeforeSpawn = _zombieQuery.CalculateEntityCount();

            bool prefabEntityValid = false;
            bool prefabHasZombieTag = false;
            bool prefabHasPrefabTag = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            bool prefabHasSpriteRenderer = false;
            float prefabScale = 0f;
#endif
            if (hasConfig && config.Prefab != Entity.Null && entityManager.Exists(config.Prefab))
            {
                prefabHasZombieTag = entityManager.HasComponent<ZombieTag>(config.Prefab);
                prefabHasPrefabTag = entityManager.HasComponent<Prefab>(config.Prefab);
                prefabEntityValid = prefabHasPrefabTag;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                prefabHasSpriteRenderer = entityManager.HasComponent<SpriteRenderer>(config.Prefab);
                if (entityManager.HasComponent<LocalTransform>(config.Prefab))
                {
                    prefabScale = entityManager.GetComponentData<LocalTransform>(config.Prefab).Scale;
                }
#endif
            }

            LogSpawnDiagnosticsOnce(
                hasConfig,
                hasMap,
                config,
                mapData,
                config.Prefab != Entity.Null,
                prefabEntityValid,
                prefabHasZombieTag,
                prefabHasPrefabTag,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                prefabHasSpriteRenderer,
                prefabScale,
#endif
                aliveCountBeforeSpawn);

            if (!hasConfig || !hasMap || !prefabEntityValid)
            {
                return;
            }

            if (config.SpawnRate <= 0f || config.SpawnBatchSize <= 0 || config.MaxAlive <= 0)
            {
                return;
            }

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

            if (aliveCountBeforeSpawn >= config.MaxAlive)
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

            int available = config.MaxAlive - aliveCountBeforeSpawn;
            spawnCount = math.min(spawnCount, available);
            if (spawnCount <= 0)
            {
                entityManager.SetComponentData(spawnStateEntity, stateData);
                return;
            }

            EntityCommandBuffer ecb = SystemAPI
                .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            LocalTransform prefabTransform = entityManager.HasComponent<LocalTransform>(config.Prefab)
                ? entityManager.GetComponentData<LocalTransform>(config.Prefab)
                : LocalTransform.FromPositionRotationScale(float3.zero, quaternion.identity, 1f);
            Unity.Mathematics.Random random = stateData.Random;
            float3 firstSpawnPosition = float3.zero;
            bool capturedFirstSpawn = false;
            for (int i = 0; i < spawnCount; i++)
            {
                int2 spawnCell = SampleSpawnRingCell(ref random, mapData.Width, mapData.Height, mapData.SpawnMargin);
                Entity entity = ecb.Instantiate(config.Prefab);

                float3 position = mapData.GridToWorld(spawnCell, 0f);
                if (!capturedFirstSpawn)
                {
                    firstSpawnPosition = position;
                    capturedFirstSpawn = true;
                }
                LocalTransform transform = LocalTransform.FromPositionRotationScale(position, prefabTransform.Rotation, prefabTransform.Scale);
                ecb.SetComponent(entity, transform);
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogFirstSpawnBatchOnce(spawnCount, firstSpawnPosition, mapData.CenterWorld);
#endif

            stateData.Random = random;
            entityManager.SetComponentData(spawnStateEntity, stateData);
        }

        private static void LogSpawnDiagnosticsOnce(
            bool hasConfig,
            bool hasMap,
            ZombieSpawnConfig config,
            MapRuntimeData mapData,
            bool prefabAssigned,
            bool prefabEntityValid,
            bool prefabHasZombieTag,
            bool prefabHasPrefabTag,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            bool prefabHasSpriteRenderer,
            float prefabScale,
#endif
            int zombieCountBeforeSpawn)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (s_loggedSpawnDiagnostics)
            {
                return;
            }

            s_loggedSpawnDiagnostics = true;

            string mapSummary = hasMap
                ? $"yes (w={mapData.Width}, h={mapData.Height}, margin={mapData.SpawnMargin})"
                : "no";

            string actionHint;
            if (!hasConfig)
            {
                actionHint = "Missing ZombieSpawnConfig singleton. Add ZombieSpawnConfigAuthoring to an active scene object, or run Tools/Survival/Setup Zombie Demo Scene.";
            }
            else if (!prefabAssigned)
            {
                actionHint = "ZombieSpawnConfig exists, but Prefab is Entity.Null. Assign _zombiePrefab on ZombieSpawnConfigAuthoring (or run Tools/Survival/Setup Zombie Demo Scene). Runtime bridge retries until a valid prefab entity is available.";
            }
            else if (!prefabEntityValid)
            {
                actionHint = "Prefab entity is invalid. Ensure assigned prefab has ZombieAuthoring and baking/runtime bridge can produce a Prefab-tagged entity.";
            }
            else if (!hasMap)
            {
                actionHint = "MapRuntimeData singleton is missing. Ensure map generation runs and MapEcsBridge sync succeeds.";
            }
            else
            {
                actionHint = "All required spawn prerequisites are present.";
            }

            Debug.Log(
                "[ZombieSpawnSystem] Diagnostics (once)\n" +
                $"- ZombieSpawnConfig singleton: {(hasConfig ? "yes" : "no")}\n" +
                $"- Prefab assigned (Entity.Null?): {(prefabAssigned ? "yes" : "no")}\n" +
                $"- Prefab entity valid: {(prefabEntityValid ? "yes" : "no")}\n" +
                $"- Prefab has ZombieTag: {(prefabHasZombieTag ? "yes" : "no")}\n" +
                $"- Prefab has Prefab tag: {(prefabHasPrefabTag ? "yes" : "no")}\n" +
                $"- Prefab has SpriteRenderer companion: {(prefabHasSpriteRenderer ? "yes" : "no")}\n" +
                $"- Prefab scale: {prefabScale}\n" +
                $"- MapRuntimeData singleton: {mapSummary}\n" +
                $"- spawnRate={config.SpawnRate}, spawnBatchSize={config.SpawnBatchSize}, maxAlive={config.MaxAlive}, seed={config.Seed}\n" +
                $"- Zombie count before spawn: {zombieCountBeforeSpawn}\n" +
                $"- Action: {actionHint}");
#endif
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static void LogFirstSpawnBatchOnce(int spawnCount, float3 firstSpawnPosition, float2 centerWorld)
        {
            if (s_loggedFirstSpawnBatch)
            {
                return;
            }

            s_loggedFirstSpawnBatch = true;
            float distanceToCenter = math.distance(firstSpawnPosition.xy, centerWorld);
            Debug.Log(
                "[ZombieSpawnSystem] First spawn batch (once)\n" +
                $"- Spawned this update: {spawnCount}\n" +
                $"- First spawn world position: ({firstSpawnPosition.x:F2}, {firstSpawnPosition.y:F2}, {firstSpawnPosition.z:F2})\n" +
                $"- Distance from map center: {distanceToCenter:F2}");
        }
#endif

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
