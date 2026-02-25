using Project.Map;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Project.Buildings
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(Project.Map.WallFieldBuildSystem))]
    public partial struct BuildingObstacleStampSystem : ISystem
    {
        private EntityQuery _mapQuery;
        private EntityQuery _registryQuery;
        private EntityQuery _unstampedBuildingsQuery;
        private int _lastObservedRectCount;
        private bool _hasObservedRectCount;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private bool _warnedUnexpectedRectGrowthWithoutStamp;
#endif

        public void OnCreate(ref SystemState state)
        {
            _mapQuery = state.GetEntityQuery(ComponentType.ReadOnly<MapRuntimeData>());
            _registryQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<DynamicObstacleRegistryTag>(),
                ComponentType.ReadWrite<DynamicObstacleRect>());
            _unstampedBuildingsQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<BuildingTag>(),
                    ComponentType.ReadOnly<BuildingFootprint>(),
                    ComponentType.ReadOnly<LocalTransform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<ObstacleStampedTag>()
                }
            });

            EnsureRegistrySingleton(ref state);

            state.RequireForUpdate<MapRuntimeData>();
            state.RequireForUpdate<BuildingTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            EnsureRegistrySingleton(ref state);
            if (_registryQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity registryEntity = _registryQuery.GetSingletonEntity();
            DynamicBuffer<DynamicObstacleRect> obstacleRects = state.EntityManager.GetBuffer<DynamicObstacleRect>(registryEntity);
            bool hasUnstampedBuildings = !_unstampedBuildingsQuery.IsEmptyIgnoreFilter;

            if (!hasUnstampedBuildings)
            {
                UpdateRectGrowthDiagnostics(obstacleRects.Length, false);
                return;
            }

            if (_mapQuery.IsEmptyIgnoreFilter)
            {
                UpdateRectGrowthDiagnostics(obstacleRects.Length, false);
                return;
            }

            Entity mapEntity = _mapQuery.GetSingletonEntity();
            MapRuntimeData map = state.EntityManager.GetComponentData<MapRuntimeData>(mapEntity);

            bool anyStamped = false;
            int stampedCount = 0;
            EntityCommandBuffer ecb = default;

            foreach (var (footprint, transform, entity) in SystemAPI
                .Query<RefRO<BuildingFootprint>, RefRO<LocalTransform>>()
                .WithAll<BuildingTag>()
                .WithNone<ObstacleStampedTag>()
                .WithEntityAccess())
            {
                int2 anchorCell = map.WorldToGrid(transform.ValueRO.Position.xy);
                int2 sizeCells = math.max(footprint.ValueRO.SizeCells, new int2(1, 1));
                int2 minCell = anchorCell + footprint.ValueRO.PivotOffsetCells;
                int2 maxCellExclusive = minCell + sizeCells;

                int2 clampedMin = math.clamp(minCell, int2.zero, new int2(map.Width, map.Height));
                int2 clampedMax = math.clamp(maxCellExclusive, int2.zero, new int2(map.Width, map.Height));

                if (clampedMin.x < clampedMax.x && clampedMin.y < clampedMax.y)
                {
                    obstacleRects.Add(new DynamicObstacleRect
                    {
                        MinCell = clampedMin,
                        MaxCellExclusive = clampedMax
                    });

                    if (!anyStamped)
                    {
                        ecb = new EntityCommandBuffer(Allocator.Temp);
                    }

                    // Mark only after the rect append has been applied.
                    ecb.AddComponent<ObstacleStampedTag>(entity);
                    anyStamped = true;
                    stampedCount++;
                }
            }

            if (!anyStamped)
            {
                UpdateRectGrowthDiagnostics(obstacleRects.Length, false);
                return;
            }

            int rectCountAfterStamp = obstacleRects.Length;
            if (!state.EntityManager.HasComponent<WallFieldDirtyTag>(mapEntity))
            {
                state.EntityManager.AddComponent<WallFieldDirtyTag>(mapEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            UpdateRectGrowthDiagnostics(rectCountAfterStamp, stampedCount > 0);
        }

        private void EnsureRegistrySingleton(ref SystemState state)
        {
            if (!_registryQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity registryEntity = state.EntityManager.CreateEntity(typeof(DynamicObstacleRegistryTag));
            state.EntityManager.AddBuffer<DynamicObstacleRect>(registryEntity);
        }

        private void UpdateRectGrowthDiagnostics(int currentRectCount, bool hadNewStamps)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!hadNewStamps &&
                _hasObservedRectCount &&
                currentRectCount > _lastObservedRectCount &&
                !_warnedUnexpectedRectGrowthWithoutStamp)
            {
                _warnedUnexpectedRectGrowthWithoutStamp = true;
                UnityEngine.Debug.LogWarning(
                    "[BuildingObstacleStampSystem] DynamicObstacleRect length increased without stamping new buildings. " +
                    "This suggests duplicate appends or external writes.");
            }
#endif

            _lastObservedRectCount = currentRectCount;
            _hasObservedRectCount = true;
        }
    }
}
