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

    public struct ZombieVelocity : IComponentData
    {
        public float2 Value;
    }

    public struct ZombieSteeringState : IComponentData
    {
        public float2 LastDirection;
    }

    public struct ZombieGoalIntent : IComponentData
    {
        public float2 Direction;
        public float StepDistance;
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

    public struct HordeSeparationConfig : IComponentData
    {
        public float Radius;
        public float CellSizeFactor;
        public float InfluenceRadiusFactor;
        public float SeparationStrength;
        public float MaxPushPerFrame;
        public int MaxNeighbors;
        public int Iterations;
    }

    public struct WallRepulsionConfig : IComponentData
    {
        public float UnitRadiusWorld;
        public float WallPushStrength;
        public float MaxWallPushPerFrame;
        public int ProjectionSearchRadiusCells;
    }

    public struct HordeHardSeparationConfig : IComponentData
    {
        public byte Enabled;
        public byte JamOnly;
        public float JamPressureThreshold;
        public float DensePressureThreshold;
        public float SlowSpeedFraction;
        public int IterationsJam;
        public int MaxNeighborsJam;
        public float MaxPushPerFrameJam;
        public float Radius;
        public float CellSize;
        public int MaxNeighbors;
        public int Iterations;
        public float MaxCorrectionPerIter;
        public float Slop;
    }

    public struct HordePressureConfig : IComponentData
    {
        public byte Enabled;
        public float TargetUnitsPerCell;
        public float PressureStrength;
        public float MaxPushPerFrame;
        public float SpeedFractionCap;
        public float BackpressureThreshold;
        public float MinSpeedFactor;
        public float BackpressureK;
        public float BackpressureMaxFactor;
        public float BlockedCellPenalty;
        public int FieldUpdateIntervalFrames;
        public int BlurPasses;
        public byte DisablePairwiseSeparationWhenPressureEnabled;
    }

    public struct ZombieAccelerationConfig : IComponentData
    {
        public byte Enabled;
        public float TimeToMaxSpeedSeconds;
        public float MaxAccel;
        public float DecelMultiplier;
    }

    public struct PressureFieldBufferTag : IComponentData
    {
    }

    public struct PressureCell : IBufferElementData
    {
        public float Value;
    }

    public struct HordeTuningQuickConfig : IComponentData
    {
        public int Enabled;
        public int LogEveryNFrames;
        public int SampleStride;
    }

    public struct HordeTuningQuickMetrics : IComponentData
    {
        public int Sampled;
        public int OverlapHits;
        public int ExactOverlapHits;
        public int JamHits;
        public int HardJamEnabledHits;
        public int CapReachedHits;
        public int ProcessedNeighborsSum;
        public int SpeedSamples;
        public int BackpressureActiveHits;
        public int AccelSamples;
        public int HardAppliedSamples;
        public int HardAppliedHits;
        public float Dt;
        public float AvgSpeed;
        public float P50Speed;
        public float P90Speed;
        public float MinSpeed;
        public float MaxSpeed;
        public float AvgAccel;
        public float AvgSpeedFraction;
        public float AvgSpeedScale;
        public float MinSpeedScale;
        public float HardAppliedDeltaSum;
    }

    public struct HordeHardSeparationDebugStats : IComponentData
    {
        public int Sampled;
        public int Applied;
        public float SumDelta;
        public float LastDt;
    }
}
