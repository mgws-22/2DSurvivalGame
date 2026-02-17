using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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

#if UNITY_EDITOR
            if (_zombiePrefab == null)
            {
                _zombiePrefab = ZombieSpawnConfigEditorResolver.FindZombiePrefab();
            }
#endif
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

#if UNITY_EDITOR
    internal static class ZombieSpawnConfigEditorResolver
    {
        public static GameObject FindZombiePrefab()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab");
            GameObject found = null;
            string foundPath = null;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponent<ZombieAuthoring>() == null)
                {
                    continue;
                }

                if (found == null || string.CompareOrdinal(path, foundPath) < 0)
                {
                    found = prefab;
                    foundPath = path;
                }
            }

            return found;
        }
    }
#endif

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
                configEntity = SelectConfigEntity(entityManager, entities);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (entity == configEntity)
                    {
                        continue;
                    }

                    if (entityManager.Exists(entity))
                    {
                        entityManager.DestroyEntity(entity);
                    }
                }
            }

            query.Dispose();

            ZombieSpawnConfig config = entityManager.GetComponentData<ZombieSpawnConfig>(configEntity);
            Entity prefabEntity = config.Prefab;
            if (!IsValidZombiePrefabEntity(entityManager, prefabEntity))
            {
                prefabEntity = FindZombiePrefabEntity(entityManager);
            }

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
            return IsValidZombiePrefabEntity(entityManager, config.Prefab);
        }

        private static bool IsValidZombiePrefabEntity(EntityManager entityManager, Entity prefabEntity)
        {
            return prefabEntity != Entity.Null &&
                   entityManager.Exists(prefabEntity) &&
                   entityManager.HasComponent<Prefab>(prefabEntity) &&
                   entityManager.HasComponent<ZombieTag>(prefabEntity);
        }

        private static Entity SelectConfigEntity(EntityManager entityManager, NativeArray<Entity> entities)
        {
            if (entities.Length == 0)
            {
                return Entity.Null;
            }

            Entity fallback = entities[0];
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (!entityManager.Exists(entity))
                {
                    continue;
                }

                ZombieSpawnConfig config = entityManager.GetComponentData<ZombieSpawnConfig>(entity);
                if (IsValidZombiePrefabEntity(entityManager, config.Prefab))
                {
                    return entity;
                }
            }

            return fallback;
        }

        private static Entity FindZombiePrefabEntity(EntityManager entityManager)
        {
            EntityQueryDesc prefabQueryDesc = new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Prefab>(),
                    ComponentType.ReadOnly<ZombieTag>()
                },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities
            };

            EntityQuery prefabQuery = entityManager.CreateEntityQuery(prefabQueryDesc);
            using NativeArray<Entity> entities = prefabQuery.ToEntityArray(Allocator.Temp);
            Entity prefabEntity = Entity.Null;
            for (int i = 0; i < entities.Length; i++)
            {
                Entity candidate = entities[i];
                if (!entityManager.Exists(candidate))
                {
                    continue;
                }

                if (!entityManager.HasComponent<ZombieMoveSpeed>(candidate))
                {
                    continue;
                }

                prefabEntity = candidate;
                break;
            }

            prefabQuery.Dispose();
            return prefabEntity;
        }

    }
}
