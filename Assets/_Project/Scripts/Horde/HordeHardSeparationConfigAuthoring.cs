using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Horde
{
    [DisallowMultipleComponent]
    public sealed class HordeHardSeparationConfigAuthoring : MonoBehaviour
    {
        [SerializeField] private bool _enabled;
        [Min(0.001f)] [SerializeField] private float _radius = 0.1f;
        [Min(0.001f)] [SerializeField] private float _cellSize = 0.1f;
        [Min(1)] [SerializeField] private int _maxNeighbors = 28;
        [Min(1)] [SerializeField] private int _iterations = 2;
        [Min(0f)] [SerializeField] private float _maxCorrectionPerIter = 0.08f;
        [Min(0f)] [SerializeField] private float _slop = 0.001f;

        public bool Enabled => _enabled;
        public float Radius => _radius;
        public float CellSize => _cellSize;
        public int MaxNeighbors => _maxNeighbors;
        public int Iterations => _iterations;
        public float MaxCorrectionPerIter => _maxCorrectionPerIter;
        public float Slop => _slop;
    }

    public sealed class HordeHardSeparationConfigAuthoringBaker : Baker<HordeHardSeparationConfigAuthoring>
    {
        public override void Bake(HordeHardSeparationConfigAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new HordeHardSeparationConfig
            {
                Enabled = authoring.Enabled ? (byte)1 : (byte)0,
                Radius = math.max(0.001f, authoring.Radius),
                CellSize = math.max(0.001f, authoring.CellSize),
                MaxNeighbors = math.max(1, authoring.MaxNeighbors),
                Iterations = math.max(1, authoring.Iterations),
                MaxCorrectionPerIter = math.max(0f, authoring.MaxCorrectionPerIter),
                Slop = math.max(0f, authoring.Slop)
            });
        }
    }
}
