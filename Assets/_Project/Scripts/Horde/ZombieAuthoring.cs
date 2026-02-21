using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Horde
{
    public sealed class ZombieAuthoring : MonoBehaviour
    {
        [Min(0f)]
        [SerializeField] private float _moveSpeed = 1f;

        public float MoveSpeed => _moveSpeed;
    }

    public sealed class ZombieAuthoringBaker : Baker<ZombieAuthoring>
    {
        public override void Bake(ZombieAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<ZombieTag>(entity);
            AddComponent(entity, new ZombieMoveSpeed
            {
                Value = math.max(0f, authoring.MoveSpeed)
            });
            AddComponent(entity, new ZombieSteeringState
            {
                LastDirection = new float2(0f, 0f)
            });
            AddComponent(entity, new ZombieGoalIntent
            {
                Direction = float2.zero,
                StepDistance = 0f
            });
        }
    }
}
