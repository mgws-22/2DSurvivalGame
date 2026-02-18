using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Project.Map
{
    public struct WallFieldBlob
    {
        public int Width;
        public int Height;
        public float CellSize;
        public float2 OriginWorld;
        public BlobArray<ushort> Dist;
        public BlobArray<byte> Dir;
        public BlobArray<float2> DirLut;
    }

    public struct WallFieldSingleton : IComponentData
    {
        public BlobAssetReference<WallFieldBlob> Blob;
    }

    public struct WallFieldDirtyTag : IComponentData
    {
    }
}
