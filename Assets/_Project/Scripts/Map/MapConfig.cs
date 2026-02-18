using System;
using UnityEngine;

namespace Project.Map
{
    [Serializable]
    public struct MapConfig
    {
        [Min(8)] public int width;
        [Min(8)] public int height;
        public int seed;

        [Min(0.1f)] public float tileSize;
        [Min(0.0001f)] public float noiseScale;
        [Range(1, 8)] public int octaves;
        [Min(1f)] public float lacunarity;
        [Range(0.05f, 1f)] public float gain;

        [Min(0f)] public float warpScale;
        [Min(0f)] public float warpStrength;
        [Range(0.01f, 0.95f)] public float bandWidth;

        [Range(0, 4)] public int smoothIterations;
        [Min(0)] public int centerOpenRadius;
        [Min(0)] public int gateCountPerSide;
        [Min(0)] public int gateRadius;
        [Min(0)] public int spawnMargin;

        public static MapConfig CreateDefault()
        {
            return new MapConfig
            {
                width = 200,
                height = 200,
                seed = 12345,
                tileSize = 1f,
                noiseScale = 0.05f,
                octaves = 3,
                lacunarity = 2.05f,
                gain = 0.45f,
                warpScale = 0.45f,
                warpStrength = 1.3f,
                bandWidth = 0.3f,
                smoothIterations = 3,
                centerOpenRadius = 7,
                gateCountPerSide = 2,
                gateRadius = 2,
                spawnMargin = 8
            };
        }

        public MapConfig GetValidated()
        {
            MapConfig validated = this;

            validated.width = Mathf.Max(8, validated.width);
            validated.height = Mathf.Max(8, validated.height);
            validated.tileSize = Mathf.Max(0.1f, validated.tileSize);

            validated.noiseScale = Mathf.Max(0.0001f, validated.noiseScale);
            validated.octaves = Mathf.Clamp(validated.octaves, 1, 8);
            validated.lacunarity = Mathf.Max(1f, validated.lacunarity);
            validated.gain = Mathf.Clamp(validated.gain, 0.05f, 1f);

            validated.warpScale = Mathf.Max(0f, validated.warpScale);
            validated.warpStrength = Mathf.Max(0f, validated.warpStrength);
            validated.bandWidth = Mathf.Clamp(validated.bandWidth, 0.01f, 0.95f);

            validated.smoothIterations = Mathf.Clamp(validated.smoothIterations, 0, 4);
            validated.centerOpenRadius = Mathf.Max(0, validated.centerOpenRadius);
            validated.gateCountPerSide = Mathf.Max(0, validated.gateCountPerSide);
            validated.gateRadius = Mathf.Max(0, validated.gateRadius);
            validated.spawnMargin = Mathf.Max(0, validated.spawnMargin);

            return validated;
        }
    }
}
