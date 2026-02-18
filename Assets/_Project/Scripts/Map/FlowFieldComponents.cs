using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Project.Map
{
    public struct FlowFieldBlob
    {
        public int Width;
        public int Height;
        public float CellSize;
        public float2 OriginWorld;
        public BlobArray<byte> Dir;
        public BlobArray<ushort> Dist;
    }

    public struct FlowFieldSingleton : IComponentData
    {
        public BlobAssetReference<FlowFieldBlob> Blob;
    }

    public struct FlowFieldDirtyTag : IComponentData
    {
    }

    public struct GatePoint : IBufferElementData
    {
        public float2 WorldPos;
    }
}
