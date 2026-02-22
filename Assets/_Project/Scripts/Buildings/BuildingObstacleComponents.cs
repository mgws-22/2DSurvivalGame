using Unity.Entities;
using Unity.Mathematics;

namespace Project.Buildings
{
    public struct BuildingTag : IComponentData
    {
    }

    public struct BuildingFootprint : IComponentData
    {
        public int2 SizeCells;
        public int2 PivotOffsetCells;
    }

    public struct ObstacleStampedTag : IComponentData
    {
    }

    public struct DynamicObstacleRegistryTag : IComponentData
    {
    }

    public struct DynamicObstacleRect : IBufferElementData
    {
        public int2 MinCell;
        public int2 MaxCellExclusive;
    }
}
