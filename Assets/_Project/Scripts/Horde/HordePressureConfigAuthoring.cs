using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Horde
{
    [DisallowMultipleComponent]
    public sealed class HordePressureConfigAuthoring : MonoBehaviour
    {
        [SerializeField] private bool _enabled = true;

        [Header("Pressure")]
        [Min(0f)] [SerializeField] private float _targetUnitsPerCell = 1.5f;
        [Min(0f)] [SerializeField] private float _pressureStrength = 1.25f;
        [Min(0f)] [SerializeField] private float _maxPushPerFrame = 2.0f;
        [Range(0f, 1f)] [SerializeField] private float _speedFractionCap = 0.45f;

        [Header("Pressure Shape")]
        [Min(0f)] [SerializeField] private float _pressureParallelScale = 0.35f;
        [Min(0f)] [SerializeField] private float _pressurePerpScale = 1.25f;
        [Min(0f)] [SerializeField] private float _wallTangentStrength = 0.75f;
        [Min(0f)] [SerializeField] private float _wallTangentMaxPushPerFrame = 1.25f;
        [Min(0f)] [SerializeField] private float _wallNearDistanceCells = 1.25f;
        [Min(0f)] [SerializeField] private float _denseUnitsPerCellThreshold = 5.0f;
        [SerializeField] private bool _enableWallTangentDriftDebug;
        [SerializeField] private bool _debugForceTangent;

        [Header("Backpressure")]
        [Min(0f)] [SerializeField] private float _backpressureThreshold = 10.0f;
        [Range(0f, 1f)] [SerializeField] private float _minSpeedFactor = 0.15f;
        [Min(0f)] [SerializeField] private float _backpressureK = 0.5f;
        [Min(0f)] [SerializeField] private float _backpressureMaxFactor = 22.0f;

        [Header("Field Build")]
        [Min(0f)] [SerializeField] private float _blockedCellPenalty = 6.0f;
        [Min(1)] [SerializeField] private int _fieldUpdateIntervalFrames = 2;
        [Range(0, 2)] [SerializeField] private int _blurPasses = 1;
        [SerializeField] private bool _disablePairwiseSeparationWhenPressureEnabled;

        public bool Enabled => _enabled;
        public float TargetUnitsPerCell => _targetUnitsPerCell;
        public float PressureStrength => _pressureStrength;
        public float MaxPushPerFrame => _maxPushPerFrame;
        public float SpeedFractionCap => _speedFractionCap;
        public float PressureParallelScale => _pressureParallelScale;
        public float PressurePerpScale => _pressurePerpScale;
        public float WallTangentStrength => _wallTangentStrength;
        public float WallTangentMaxPushPerFrame => _wallTangentMaxPushPerFrame;
        public float WallNearDistanceCells => _wallNearDistanceCells;
        public float DenseUnitsPerCellThreshold => _denseUnitsPerCellThreshold;
        public bool EnableWallTangentDriftDebug => _enableWallTangentDriftDebug;
        public bool DebugForceTangent => _debugForceTangent;
        public float BackpressureThreshold => _backpressureThreshold;
        public float MinSpeedFactor => _minSpeedFactor;
        public float BackpressureK => _backpressureK;
        public float BackpressureMaxFactor => _backpressureMaxFactor;
        public float BlockedCellPenalty => _blockedCellPenalty;
        public int FieldUpdateIntervalFrames => _fieldUpdateIntervalFrames;
        public int BlurPasses => _blurPasses;
        public bool DisablePairwiseSeparationWhenPressureEnabled => _disablePairwiseSeparationWhenPressureEnabled;
    }

    public sealed class HordePressureConfigAuthoringBaker : Baker<HordePressureConfigAuthoring>
    {
        public override void Bake(HordePressureConfigAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new HordePressureConfig
            {
                Enabled = authoring.Enabled ? (byte)1 : (byte)0,
                TargetUnitsPerCell = math.max(0f, authoring.TargetUnitsPerCell),
                PressureStrength = math.max(0f, authoring.PressureStrength),
                MaxPushPerFrame = math.max(0f, authoring.MaxPushPerFrame),
                SpeedFractionCap = math.clamp(authoring.SpeedFractionCap, 0f, 1f),
                PressureParallelScale = math.max(0f, authoring.PressureParallelScale),
                PressurePerpScale = math.max(0f, authoring.PressurePerpScale),
                WallTangentStrength = math.max(0f, authoring.WallTangentStrength),
                WallTangentMaxPushPerFrame = math.max(0f, authoring.WallTangentMaxPushPerFrame),
                WallNearDistanceCells = math.max(0f, authoring.WallNearDistanceCells),
                DenseUnitsPerCellThreshold = math.max(0f, authoring.DenseUnitsPerCellThreshold),
                BackpressureThreshold = math.max(0f, authoring.BackpressureThreshold),
                MinSpeedFactor = math.clamp(authoring.MinSpeedFactor, 0f, 1f),
                BackpressureK = math.max(0f, authoring.BackpressureK),
                BackpressureMaxFactor = math.max(0f, authoring.BackpressureMaxFactor),
                BlockedCellPenalty = math.max(0f, authoring.BlockedCellPenalty),
                FieldUpdateIntervalFrames = math.max(1, authoring.FieldUpdateIntervalFrames),
                BlurPasses = math.clamp(authoring.BlurPasses, 0, 2),
                DisablePairwiseSeparationWhenPressureEnabled = authoring.DisablePairwiseSeparationWhenPressureEnabled ? (byte)1 : (byte)0,
                EnableWallTangentDriftDebug = authoring.EnableWallTangentDriftDebug ? (byte)1 : (byte)0,
                DebugForceTangent = authoring.DebugForceTangent ? (byte)1 : (byte)0
            });
        }
    }
}
