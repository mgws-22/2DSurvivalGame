using Unity.Entities;
using Unity.Mathematics;

namespace Project.Buildings.Placement
{
    public enum BuildingPlacementType : byte
    {
        None = 0,
        Wall = 1
    }

    public struct BuildingPrefabCatalog : IComponentData
    {
        public Entity WallPrefab;
    }

    public struct PlaceBuildingRequestQueueTag : IComponentData
    {
    }

    public struct PlaceBuildingRequest : IBufferElementData
    {
        public byte Type;
        public int2 Cell;
        public float3 WorldPos;
    }
}
