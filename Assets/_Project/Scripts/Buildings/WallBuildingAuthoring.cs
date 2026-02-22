using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Buildings
{
    [DisallowMultipleComponent]
    public sealed class WallBuildingAuthoring : MonoBehaviour
    {
        [SerializeField] private Vector2Int _sizeCells = new Vector2Int(1, 1);
        [SerializeField] private Vector2Int _pivotOffsetCells = Vector2Int.zero;

        public Vector2Int SizeCells => _sizeCells;
        public Vector2Int PivotOffsetCells => _pivotOffsetCells;
    }

    public sealed class WallBuildingAuthoringBaker : Baker<WallBuildingAuthoring>
    {
        public override void Bake(WallBuildingAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);

            int2 sizeCells = new int2(
                math.max(1, authoring.SizeCells.x),
                math.max(1, authoring.SizeCells.y));

            int2 pivotOffsetCells = new int2(
                authoring.PivotOffsetCells.x,
                authoring.PivotOffsetCells.y);

            AddComponent<BuildingTag>(entity);
            AddComponent(entity, new BuildingFootprint
            {
                SizeCells = sizeCells,
                PivotOffsetCells = pivotOffsetCells
            });
        }
    }
}
