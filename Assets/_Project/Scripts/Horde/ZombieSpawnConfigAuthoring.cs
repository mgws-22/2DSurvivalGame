using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

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
}
