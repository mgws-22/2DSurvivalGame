using Project.Map;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Buildings.Placement
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(Project.Buildings.BuildingObstacleStampSystem))]
    public partial struct BuildingPlacementSystem : ISystem
    {
        private EntityQuery _requestQueueQuery;

        public void OnCreate(ref SystemState state)
        {
            _requestQueueQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlaceBuildingRequestQueueTag>(),
                ComponentType.ReadWrite<PlaceBuildingRequest>());

            EnsureRequestQueueSingleton(ref state);

            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<BuildingPrefabCatalog>();
            state.RequireForUpdate<PlaceBuildingRequestQueueTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            EnsureRequestQueueSingleton(ref state);
            if (_requestQueueQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity requestQueueEntity = _requestQueueQuery.GetSingletonEntity();
            DynamicBuffer<PlaceBuildingRequest> requests = state.EntityManager.GetBuffer<PlaceBuildingRequest>(requestQueueEntity);
            if (requests.Length == 0)
            {
                return;
            }

            // Intentionally require map presence even though snapping/validation happened on the input side.
            _ = SystemAPI.GetSingleton<MapRuntimeData>();

            BuildingPrefabCatalog catalog = SystemAPI.GetSingleton<BuildingPrefabCatalog>();
            if (catalog.WallPrefab == Entity.Null || !state.EntityManager.Exists(catalog.WallPrefab))
            {
                requests.Clear();
                return;
            }

            bool prefabHasLocalTransform = state.EntityManager.HasComponent<LocalTransform>(catalog.WallPrefab);
            LocalTransform prefabTransform = prefabHasLocalTransform
                ? state.EntityManager.GetComponentData<LocalTransform>(catalog.WallPrefab)
                : LocalTransform.FromPositionRotationScale(float3.zero, quaternion.identity, 1f);

            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < requests.Length; i++)
            {
                PlaceBuildingRequest request = requests[i];
                if (request.Type != (byte)BuildingPlacementType.Wall)
                {
                    continue;
                }

                Entity wallEntity = ecb.Instantiate(catalog.WallPrefab);
                float3 position = request.WorldPos;
                position.z = 0f;

                LocalTransform transform = LocalTransform.FromPositionRotationScale(
                    position,
                    prefabTransform.Rotation,
                    prefabTransform.Scale);

                if (prefabHasLocalTransform)
                {
                    ecb.SetComponent(wallEntity, transform);
                }
                else
                {
                    ecb.AddComponent(wallEntity, transform);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            requests.Clear();
        }

        private void EnsureRequestQueueSingleton(ref SystemState state)
        {
            if (!_requestQueueQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity entity = state.EntityManager.CreateEntity(typeof(PlaceBuildingRequestQueueTag));
            state.EntityManager.AddBuffer<PlaceBuildingRequest>(entity);
        }
    }
}
