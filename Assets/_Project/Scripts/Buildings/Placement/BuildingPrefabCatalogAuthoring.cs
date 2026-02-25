using Project.Buildings;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Scenes;
using UnityEngine;

namespace Project.Buildings.Placement
{
    [DisallowMultipleComponent]
    public sealed class BuildingPrefabCatalogAuthoring : MonoBehaviour
    {
        [SerializeField] private GameObject _wallPrefab;

        private bool _pendingRuntimeSync;

        public GameObject WallPrefab
        {
            get => _wallPrefab;
            set => _wallPrefab = value;
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            _pendingRuntimeSync = !BuildingPrefabCatalogRuntimeBridge.Sync(this);
        }

        private void LateUpdate()
        {
            if (!_pendingRuntimeSync)
            {
                return;
            }

            _pendingRuntimeSync = !BuildingPrefabCatalogRuntimeBridge.Sync(this);
        }
    }

    public sealed class BuildingPrefabCatalogAuthoringBaker : Baker<BuildingPrefabCatalogAuthoring>
    {
        public override void Bake(BuildingPrefabCatalogAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            Entity wallPrefab = authoring.WallPrefab != null
                ? GetEntity(authoring.WallPrefab, TransformUsageFlags.Dynamic)
                : Entity.Null;

            AddComponent(entity, new BuildingPrefabCatalog
            {
                WallPrefab = wallPrefab
            });
        }
    }

    internal static class BuildingPrefabCatalogRuntimeBridge
    {
        private static Entity s_prefabLoadRequestEntity;
        private static EntityPrefabReference s_prefabLoadReference;

        public static bool Sync(BuildingPrefabCatalogAuthoring authoring)
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
            EntityQuery query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<BuildingPrefabCatalog>());
            int catalogCount = query.CalculateEntityCount();

            Entity catalogEntity;
            if (catalogCount == 0)
            {
                catalogEntity = entityManager.CreateEntity(typeof(BuildingPrefabCatalog));
            }
            else
            {
                using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                catalogEntity = entities.Length > 0 ? entities[0] : entityManager.CreateEntity(typeof(BuildingPrefabCatalog));

                for (int i = 1; i < entities.Length; i++)
                {
                    Entity duplicate = entities[i];
                    if (entityManager.Exists(duplicate))
                    {
                        entityManager.DestroyEntity(duplicate);
                    }
                }
            }

            query.Dispose();

            BuildingPrefabCatalog catalog = entityManager.GetComponentData<BuildingPrefabCatalog>(catalogEntity);
            Entity wallPrefabEntity = catalog.WallPrefab;

            if (!IsValidWallPrefabEntity(entityManager, wallPrefabEntity))
            {
                wallPrefabEntity = FindWallPrefabEntity(entityManager);
            }

            if (!IsValidWallPrefabEntity(entityManager, wallPrefabEntity))
            {
                TryResolvePrefabFromEntityPrefabReference(entityManager, authoring.WallPrefab, out wallPrefabEntity);
            }

            if (!IsValidWallPrefabEntity(entityManager, wallPrefabEntity))
            {
                wallPrefabEntity = Entity.Null;
            }

            catalog.WallPrefab = wallPrefabEntity;
            entityManager.SetComponentData(catalogEntity, catalog);
            return IsValidWallPrefabEntity(entityManager, catalog.WallPrefab);
        }

        private static bool IsValidWallPrefabEntity(EntityManager entityManager, Entity prefabEntity)
        {
            return prefabEntity != Entity.Null &&
                   entityManager.Exists(prefabEntity) &&
                   entityManager.HasComponent<Prefab>(prefabEntity) &&
                   entityManager.HasComponent<BuildingTag>(prefabEntity) &&
                   entityManager.HasComponent<BuildingFootprint>(prefabEntity);
        }

        private static Entity FindWallPrefabEntity(EntityManager entityManager)
        {
            EntityQueryDesc prefabQueryDesc = new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Prefab>(),
                    ComponentType.ReadOnly<BuildingTag>(),
                    ComponentType.ReadOnly<BuildingFootprint>()
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

                prefabEntity = candidate;
                break;
            }

            prefabQuery.Dispose();
            return prefabEntity;
        }

        private static bool TryResolvePrefabFromEntityPrefabReference(
            EntityManager entityManager,
            GameObject wallPrefab,
            out Entity prefabEntity)
        {
            prefabEntity = Entity.Null;
            if (wallPrefab == null)
            {
                return false;
            }

            EntityPrefabReference prefabReference = new EntityPrefabReference(wallPrefab);
            if (!prefabReference.IsReferenceValid)
            {
                return false;
            }

            if (s_prefabLoadRequestEntity == Entity.Null || !entityManager.Exists(s_prefabLoadRequestEntity))
            {
                s_prefabLoadRequestEntity = entityManager.CreateEntity(typeof(RequestEntityPrefabLoaded));
                entityManager.SetComponentData(s_prefabLoadRequestEntity, new RequestEntityPrefabLoaded
                {
                    Prefab = prefabReference
                });
                s_prefabLoadReference = prefabReference;
                return false;
            }

            if (s_prefabLoadReference != prefabReference)
            {
                s_prefabLoadReference = prefabReference;
                if (entityManager.Exists(s_prefabLoadRequestEntity))
                {
                    entityManager.DestroyEntity(s_prefabLoadRequestEntity);
                }

                s_prefabLoadRequestEntity = entityManager.CreateEntity(typeof(RequestEntityPrefabLoaded));
                entityManager.SetComponentData(s_prefabLoadRequestEntity, new RequestEntityPrefabLoaded
                {
                    Prefab = prefabReference
                });
                return false;
            }

            if (entityManager.HasComponent<RequestEntityPrefabLoaded>(s_prefabLoadRequestEntity))
            {
                entityManager.SetComponentData(s_prefabLoadRequestEntity, new RequestEntityPrefabLoaded
                {
                    Prefab = prefabReference
                });
            }
            else
            {
                entityManager.AddComponentData(s_prefabLoadRequestEntity, new RequestEntityPrefabLoaded
                {
                    Prefab = prefabReference
                });
            }

            if (!entityManager.HasComponent<PrefabLoadResult>(s_prefabLoadRequestEntity))
            {
                return false;
            }

            PrefabLoadResult result = entityManager.GetComponentData<PrefabLoadResult>(s_prefabLoadRequestEntity);
            if (!IsValidWallPrefabEntity(entityManager, result.PrefabRoot))
            {
                return false;
            }

            prefabEntity = result.PrefabRoot;
            return true;
        }
    }
}
