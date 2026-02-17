using Unity.Entities;
using Unity.Mathematics;

namespace Project.Horde
{
    public struct ZombieTag : IComponentData
    {
    }

    public struct ZombieMoveSpeed : IComponentData
    {
        public float Value;
    }

    public struct ZombieSteeringState : IComponentData
    {
        public float2 LastDirection;
    }

    public struct ZombieSpawnConfig : IComponentData
    {
        public float SpawnRate;
        public int SpawnBatchSize;
        public int MaxAlive;
        public uint Seed;
        public Entity Prefab;
    }

    public struct ZombieSpawnState : IComponentData
    {
        public Unity.Mathematics.Random Random;
        public uint LastSeed;
        public float SpawnAccumulator;
    }
}
