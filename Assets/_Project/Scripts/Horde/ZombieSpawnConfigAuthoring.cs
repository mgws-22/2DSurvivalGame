using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Horde
{
    public sealed class ZombieSpawnConfigAuthoring : MonoBehaviour
    {
        [Header("Spawn")]
        [SerializeField] private GameObject _zombiePrefab;
        [Min(0f)] [SerializeField] private float _spawnRate = 8f;
        [Min(1)] [SerializeField] private int _spawnBatchSize = 1;
        [Min(1)] [SerializeField] private int _maxAlive = 256;
        [SerializeField] private int _seed = 12345;

        public GameObject ZombiePrefab => _zombiePrefab;
        public float SpawnRate => _spawnRate;
        public int SpawnBatchSize => _spawnBatchSize;
        public int MaxAlive => _maxAlive;
        public int Seed => _seed;

        private bool _pendingRuntimeSync;

        private void OnEnable()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            _pendingRuntimeSync = !ZombieSpawnConfigRuntimeBridge.Sync(this);
        }

        private void LateUpdate()
        {
            if (!_pendingRuntimeSync)
            {
                return;
            }

            _pendingRuntimeSync = !ZombieSpawnConfigRuntimeBridge.Sync(this);
        }
    }

    public sealed class ZombieSpawnConfigAuthoringBaker : Baker<ZombieSpawnConfigAuthoring>
    {
        public override void Bake(ZombieSpawnConfigAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);

            Entity prefab = Entity.Null;
            if (authoring.ZombiePrefab != null)
            {
                prefab = GetEntity(authoring.ZombiePrefab, TransformUsageFlags.Dynamic);
            }

            AddComponent(entity, new ZombieSpawnConfig
            {
                SpawnRate = math.max(0f, authoring.SpawnRate),
                SpawnBatchSize = math.max(1, authoring.SpawnBatchSize),
                MaxAlive = math.max(1, authoring.MaxAlive),
                Seed = (uint)authoring.Seed,
                Prefab = prefab
            });
        }
    }

    internal static class ZombieSpawnConfigRuntimeBridge
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static bool s_loggedMissingPrefab;
        private static bool s_loggedMissingZombieAuthoring;
#endif

        public static bool Sync(ZombieSpawnConfigAuthoring authoring)
        {
            if (authoring == null)
            {
                return true;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            EntityManager entityManager = world.EntityManager;
            EntityQuery query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<ZombieSpawnConfig>());
            int configCount = query.CalculateEntityCount();

            Entity configEntity;
            if (configCount == 0)
            {
                configEntity = entityManager.CreateEntity(typeof(ZombieSpawnConfig));
            }
            else
            {
                using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                configEntity = entities[0];

                for (int i = 1; i < entities.Length; i++)
                {
                    if (entityManager.Exists(entities[i]))
                    {
                        entityManager.DestroyEntity(entities[i]);
                    }
                }
            }

            query.Dispose();

            ZombieSpawnConfig config = entityManager.GetComponentData<ZombieSpawnConfig>(configEntity);
            Entity prefabEntity = config.Prefab;
            if (!IsValidZombiePrefabEntity(entityManager, prefabEntity))
            {
                prefabEntity = Entity.Null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (authoring.ZombiePrefab == null)
                {
                    if (!s_loggedMissingPrefab)
                    {
                        s_loggedMissingPrefab = true;
                        Debug.LogWarning("[ZombieSpawnConfigRuntimeBridge] Missing Zombie Prefab reference on ZombieSpawnConfigAuthoring.");
                    }
                }
                else if (authoring.ZombiePrefab.GetComponent<ZombieAuthoring>() == null)
                {
                    if (!s_loggedMissingZombieAuthoring)
                    {
                        s_loggedMissingZombieAuthoring = true;
                        Debug.LogWarning("[ZombieSpawnConfigRuntimeBridge] Assigned zombie prefab is missing ZombieAuthoring component.");
                    }
                }
#endif
            }

            config.SpawnRate = math.max(0f, authoring.SpawnRate);
            config.SpawnBatchSize = math.max(1, authoring.SpawnBatchSize);
            config.MaxAlive = math.max(1, authoring.MaxAlive);
            config.Seed = (uint)authoring.Seed;
            config.Prefab = prefabEntity;

            entityManager.SetComponentData(configEntity, config);
            return true;
        }

        private static bool IsValidZombiePrefabEntity(EntityManager entityManager, Entity prefabEntity)
        {
            return prefabEntity != Entity.Null &&
                   entityManager.Exists(prefabEntity) &&
                   entityManager.HasComponent<Prefab>(prefabEntity) &&
                   entityManager.HasComponent<ZombieTag>(prefabEntity);
        }

    }
}
