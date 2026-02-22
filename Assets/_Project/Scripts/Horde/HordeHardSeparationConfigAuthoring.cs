using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Horde
{
    [DisallowMultipleComponent]
    public sealed class HordeHardSeparationConfigAuthoring : MonoBehaviour
    {
        [SerializeField] private bool _enabled = true;
        [SerializeField] private bool _jamOnly = true;
        [Min(0f)] [SerializeField] private float _jamPressureThreshold;
        [Min(0f)] [SerializeField] private float _densePressureThreshold;
        [Range(0f, 1f)] [SerializeField] private float _slowSpeedFraction = 0.2f;
        [Min(1)] [SerializeField] private int _iterationsJam = 3;
        [Min(1)] [SerializeField] private int _maxNeighborsJam = 32;
        [Min(0f)] [SerializeField] private float _maxPushPerFrameJam = 0.08f;
        [Min(0.001f)] [SerializeField] private float _radius = 0.4f;
        [Min(0.001f)] [SerializeField] private float _cellSize = 0.1f;
        [Min(1)] [SerializeField] private int _maxNeighbors = 28;
        [Min(1)] [SerializeField] private int _iterations = 2;
        [Min(0f)] [SerializeField] private float _maxCorrectionPerIter = 0.08f;
        [Min(0f)] [SerializeField] private float _slop = 0.001f;

        public bool Enabled => _enabled;
        public bool JamOnly => _jamOnly;
        public float JamPressureThreshold => _jamPressureThreshold;
        public float DensePressureThreshold => _densePressureThreshold;
        public float SlowSpeedFraction => _slowSpeedFraction;
        public int IterationsJam => _iterationsJam;
        public int MaxNeighborsJam => _maxNeighborsJam;
        public float MaxPushPerFrameJam => _maxPushPerFrameJam;
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
                JamOnly = authoring.JamOnly ? (byte)1 : (byte)0,
                JamPressureThreshold = math.max(0f, authoring.JamPressureThreshold),
                DensePressureThreshold = math.max(0f, authoring.DensePressureThreshold),
                SlowSpeedFraction = authoring.SlowSpeedFraction > 0f ? math.clamp(authoring.SlowSpeedFraction, 0f, 1f) : 0.2f,
                IterationsJam = math.max(1, authoring.IterationsJam),
                MaxNeighborsJam = math.max(1, authoring.MaxNeighborsJam),
                MaxPushPerFrameJam = math.max(0f, authoring.MaxPushPerFrameJam),
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
