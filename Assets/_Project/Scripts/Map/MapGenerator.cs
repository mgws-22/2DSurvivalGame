using Unity.Mathematics;

namespace Project.Map
{
    public static class MapGenerator
    {
        private static readonly int2[] CardinalDirections =
        {
            new int2(1, 0),
            new int2(-1, 0),
            new int2(0, 1),
            new int2(0, -1)
        };

        public static MapData Generate(MapConfig inputConfig, float2 worldOrigin)
        {
            MapConfig config = inputConfig.GetValidated();
            int width = config.width;
            int height = config.height;
            int tileCount = width * height;

            bool[] walkable = new bool[tileCount];
            bool[] smoothScratch = config.smoothIterations > 0 ? new bool[tileCount] : null;
            bool[] reachable = new bool[tileCount];
            int[] queue = new int[tileCount];
            int[] distanceField = new int[tileCount];

            int gateCapacity = math.max(1, config.gateCountPerSide) * 4;
            int2[] gateScratch = new int2[gateCapacity];

            Unity.Mathematics.Random noiseRng = CreateRandom(config.seed, 0xA341316Cu);
            Unity.Mathematics.Random gateRng = CreateRandom(config.seed, 0xC8013EA4u);

            FillBandPassNoise(config, walkable, ref noiseRng);
            ApplySmoothing(config, walkable, smoothScratch);
            ForceCenterAreaOpen(config, walkable);

            int gateCount = BuildGateCenters(config, gateScratch, ref gateRng);
            OpenGateRegions(config, walkable, gateScratch, gateCount);

            int centerStartIndex = FindCenterStartIndex(config, walkable);
            if (centerStartIndex < 0)
            {
                centerStartIndex = (width / 2) + ((height / 2) * width);
                walkable[centerStartIndex] = true;
            }

            FloodFillReachable(config, walkable, centerStartIndex, reachable, queue);
            CullUnreachableGround(walkable, reachable);

            FloodFillReachable(config, walkable, centerStartIndex, reachable, queue);
            for (int i = 0; i < gateCount; i++)
            {
                int2 gateCenter = gateScratch[i];
                if (IsGateConnected(config, walkable, reachable, gateCenter, config.gateRadius))
                {
                    continue;
                }

                BuildDistanceField(config, reachable, distanceField, queue);

                Unity.Mathematics.Random corridorRng = CreateRandom(config.seed, (uint)(0x9E3779B9u + (uint)i * 0x85EBCA6Bu));
                CarveCorridorTowardReachable(config, walkable, distanceField, gateCenter, math.max(1, config.gateRadius), ref corridorRng);

                FloodFillReachable(config, walkable, centerStartIndex, reachable, queue);
            }

            CullUnreachableGround(walkable, reachable);

            int2[] finalGateCenters = new int2[gateCount];
            for (int i = 0; i < gateCount; i++)
            {
                finalGateCenters[i] = gateScratch[i];
            }

            MapData mapData = new MapData(width, height, config.tileSize, config.spawnMargin, config.centerOpenRadius, worldOrigin, finalGateCenters);
            mapData.FillFromWalkable(walkable);
            return mapData;
        }

        private static void FillBandPassNoise(MapConfig config, bool[] walkable, ref Unity.Mathematics.Random rng)
        {
            int width = config.width;
            int height = config.height;

            float2 noiseOffset = new float2(rng.NextFloat(-10000f, 10000f), rng.NextFloat(-10000f, 10000f));
            float2 warpOffsetA = new float2(rng.NextFloat(-10000f, 10000f), rng.NextFloat(-10000f, 10000f));
            float2 warpOffsetB = new float2(rng.NextFloat(-10000f, 10000f), rng.NextFloat(-10000f, 10000f));

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = rowOffset + x;
                    float2 samplePosition = (new float2(x, y) * config.noiseScale) + noiseOffset;

                    if (config.warpStrength > 0f && config.warpScale > 0f)
                    {
                        float2 warpInput = samplePosition * config.warpScale;
                        float2 warp = new float2(
                            noise.snoise(warpInput + warpOffsetA),
                            noise.snoise(warpInput + warpOffsetB));
                        samplePosition += warp * config.warpStrength;
                    }

                    float noiseValue = SampleFbm(samplePosition, config.octaves, config.lacunarity, config.gain);
                    walkable[index] = math.abs(noiseValue) < config.bandWidth;
                }
            }
        }

        private static float SampleFbm(float2 samplePosition, int octaves, float lacunarity, float gain)
        {
            float amplitude = 1f;
            float frequency = 1f;
            float amplitudeSum = 0f;
            float value = 0f;

            for (int octave = 0; octave < octaves; octave++)
            {
                value += noise.snoise(samplePosition * frequency) * amplitude;
                amplitudeSum += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }

            return amplitudeSum > 0f ? value / amplitudeSum : 0f;
        }

        private static void ApplySmoothing(MapConfig config, bool[] walkable, bool[] smoothScratch)
        {
            if (config.smoothIterations <= 0 || smoothScratch == null)
            {
                return;
            }

            int width = config.width;
            int height = config.height;

            for (int iteration = 0; iteration < config.smoothIterations; iteration++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = x + (y * width);
                        int neighbors = CountGroundNeighbors(walkable, width, height, x, y);

                        if (walkable[index])
                        {
                            smoothScratch[index] = neighbors >= 2;
                        }
                        else
                        {
                            smoothScratch[index] = neighbors >= 5;
                        }
                    }
                }

                for (int i = 0; i < walkable.Length; i++)
                {
                    walkable[i] = smoothScratch[i];
                }
            }
        }

        private static int CountGroundNeighbors(bool[] walkable, int width, int height, int x, int y)
        {
            int count = 0;
            for (int ny = y - 1; ny <= y + 1; ny++)
            {
                if (ny < 0 || ny >= height)
                {
                    continue;
                }

                int rowOffset = ny * width;
                for (int nx = x - 1; nx <= x + 1; nx++)
                {
                    if (nx < 0 || nx >= width || (nx == x && ny == y))
                    {
                        continue;
                    }

                    if (walkable[rowOffset + nx])
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void ForceCenterAreaOpen(MapConfig config, bool[] walkable)
        {
            if (config.centerOpenRadius <= 0)
            {
                return;
            }

            int2 center = new int2(config.width / 2, config.height / 2);
            CarveDisk(config, walkable, center, config.centerOpenRadius);
        }

        private static int BuildGateCenters(MapConfig config, int2[] gateCenters, ref Unity.Mathematics.Random rng)
        {
            if (config.gateCountPerSide <= 0)
            {
                return 0;
            }

            int gateIndex = 0;
            gateIndex = BuildGateCentersForSide(config.width, 0, true, config.gateCountPerSide, config.gateRadius, gateCenters, gateIndex, ref rng);
            gateIndex = BuildGateCentersForSide(config.width, config.height - 1, true, config.gateCountPerSide, config.gateRadius, gateCenters, gateIndex, ref rng);
            gateIndex = BuildGateCentersForSide(config.height, 0, false, config.gateCountPerSide, config.gateRadius, gateCenters, gateIndex, ref rng);
            gateIndex = BuildGateCentersForSide(config.height, config.width - 1, false, config.gateCountPerSide, config.gateRadius, gateCenters, gateIndex, ref rng);

            return gateIndex;
        }

        private static int BuildGateCentersForSide(
            int sideLength,
            int fixedCoordinate,
            bool varyX,
            int gateCount,
            int gateRadius,
            int2[] gateCenters,
            int gateIndex,
            ref Unity.Mathematics.Random rng)
        {
            float spacing = (sideLength - 1f) / (gateCount + 1f);
            float jitterRange = spacing * 0.35f;

            int minAlong = math.min(gateRadius, sideLength - 1);
            int maxAlong = math.max(minAlong, (sideLength - 1) - gateRadius);

            for (int i = 0; i < gateCount; i++)
            {
                float expected = (i + 1f) * spacing;
                int along = (int)math.round(expected + rng.NextFloat(-jitterRange, jitterRange));
                along = math.clamp(along, minAlong, maxAlong);

                gateCenters[gateIndex++] = varyX
                    ? new int2(along, fixedCoordinate)
                    : new int2(fixedCoordinate, along);
            }

            return gateIndex;
        }

        private static void OpenGateRegions(MapConfig config, bool[] walkable, int2[] gateCenters, int gateCount)
        {
            for (int i = 0; i < gateCount; i++)
            {
                CarveDisk(config, walkable, gateCenters[i], config.gateRadius);
            }
        }

        private static void CarveDisk(MapConfig config, bool[] walkable, int2 center, int radius)
        {
            int width = config.width;
            int height = config.height;

            int minY = math.max(0, center.y - radius);
            int maxY = math.min(height - 1, center.y + radius);
            int radiusSq = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                int dy = y - center.y;
                int dySq = dy * dy;
                int rowOffset = y * width;

                int minX = math.max(0, center.x - radius);
                int maxX = math.min(width - 1, center.x + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - center.x;
                    if ((dx * dx) + dySq <= radiusSq)
                    {
                        walkable[rowOffset + x] = true;
                    }
                }
            }
        }

        private static int FindCenterStartIndex(MapConfig config, bool[] walkable)
        {
            int width = config.width;
            int height = config.height;
            int2 center = new int2(width / 2, height / 2);
            int centerIndex = center.x + (center.y * width);
            if (walkable[centerIndex])
            {
                return centerIndex;
            }

            int maxRadius = math.max(width, height);
            for (int radius = 1; radius <= maxRadius; radius++)
            {
                int minY = math.max(0, center.y - radius);
                int maxY = math.min(height - 1, center.y + radius);
                int minX = math.max(0, center.x - radius);
                int maxX = math.min(width - 1, center.x + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    int top = x + (minY * width);
                    if (walkable[top])
                    {
                        return top;
                    }

                    int bottom = x + (maxY * width);
                    if (walkable[bottom])
                    {
                        return bottom;
                    }
                }

                for (int y = minY + 1; y < maxY; y++)
                {
                    int left = minX + (y * width);
                    if (walkable[left])
                    {
                        return left;
                    }

                    int right = maxX + (y * width);
                    if (walkable[right])
                    {
                        return right;
                    }
                }
            }

            for (int i = 0; i < walkable.Length; i++)
            {
                if (walkable[i])
                {
                    return i;
                }
            }

            return -1;
        }

        private static void FloodFillReachable(MapConfig config, bool[] walkable, int startIndex, bool[] reachable, int[] queue)
        {
            for (int i = 0; i < reachable.Length; i++)
            {
                reachable[i] = false;
            }

            if (startIndex < 0 || !walkable[startIndex])
            {
                return;
            }

            int width = config.width;
            int height = config.height;

            int head = 0;
            int tail = 0;

            queue[tail++] = startIndex;
            reachable[startIndex] = true;

            while (head < tail)
            {
                int current = queue[head++];
                int y = current / width;
                int x = current - (y * width);

                for (int i = 0; i < CardinalDirections.Length; i++)
                {
                    int2 next = new int2(x, y) + CardinalDirections[i];
                    if (next.x < 0 || next.y < 0 || next.x >= width || next.y >= height)
                    {
                        continue;
                    }

                    int nextIndex = next.x + (next.y * width);
                    if (reachable[nextIndex] || !walkable[nextIndex])
                    {
                        continue;
                    }

                    reachable[nextIndex] = true;
                    queue[tail++] = nextIndex;
                }
            }
        }

        private static void CullUnreachableGround(bool[] walkable, bool[] reachable)
        {
            for (int i = 0; i < walkable.Length; i++)
            {
                if (walkable[i] && !reachable[i])
                {
                    walkable[i] = false;
                }
            }
        }

        private static bool IsGateConnected(MapConfig config, bool[] walkable, bool[] reachable, int2 gateCenter, int gateRadius)
        {
            int width = config.width;
            int height = config.height;

            int radius = math.max(0, gateRadius);
            int radiusSq = radius * radius;

            int minY = math.max(0, gateCenter.y - radius);
            int maxY = math.min(height - 1, gateCenter.y + radius);

            for (int y = minY; y <= maxY; y++)
            {
                int rowOffset = y * width;
                int dy = y - gateCenter.y;
                int dySq = dy * dy;

                int minX = math.max(0, gateCenter.x - radius);
                int maxX = math.min(width - 1, gateCenter.x + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - gateCenter.x;
                    if ((dx * dx) + dySq > radiusSq)
                    {
                        continue;
                    }

                    int index = rowOffset + x;
                    if (walkable[index] && reachable[index])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void BuildDistanceField(MapConfig config, bool[] reachable, int[] distanceField, int[] queue)
        {
            int width = config.width;
            int height = config.height;
            int tileCount = width * height;

            for (int i = 0; i < tileCount; i++)
            {
                distanceField[i] = -1;
            }

            int head = 0;
            int tail = 0;

            for (int i = 0; i < tileCount; i++)
            {
                if (!reachable[i])
                {
                    continue;
                }

                distanceField[i] = 0;
                queue[tail++] = i;
            }

            while (head < tail)
            {
                int current = queue[head++];
                int y = current / width;
                int x = current - (y * width);
                int nextDistance = distanceField[current] + 1;

                for (int i = 0; i < CardinalDirections.Length; i++)
                {
                    int2 next = new int2(x, y) + CardinalDirections[i];
                    if (next.x < 0 || next.y < 0 || next.x >= width || next.y >= height)
                    {
                        continue;
                    }

                    int nextIndex = next.x + (next.y * width);
                    if (distanceField[nextIndex] >= 0)
                    {
                        continue;
                    }

                    distanceField[nextIndex] = nextDistance;
                    queue[tail++] = nextIndex;
                }
            }
        }

        private static void CarveCorridorTowardReachable(
            MapConfig config,
            bool[] walkable,
            int[] distanceField,
            int2 start,
            int corridorRadius,
            ref Unity.Mathematics.Random rng)
        {
            int width = config.width;
            int height = config.height;

            int2 current = new int2(
                math.clamp(start.x, 0, width - 1),
                math.clamp(start.y, 0, height - 1));

            int maxSteps = width * height;
            for (int step = 0; step < maxSteps; step++)
            {
                CarveDisk(config, walkable, current, corridorRadius);

                int currentIndex = current.x + (current.y * width);
                int currentDistance = distanceField[currentIndex];
                if (currentDistance <= 0)
                {
                    return;
                }

                int bestDistance = int.MaxValue;
                for (int i = 0; i < CardinalDirections.Length; i++)
                {
                    int2 neighbor = current + CardinalDirections[i];
                    if (neighbor.x < 0 || neighbor.y < 0 || neighbor.x >= width || neighbor.y >= height)
                    {
                        continue;
                    }

                    int neighborIndex = neighbor.x + (neighbor.y * width);
                    int neighborDistance = distanceField[neighborIndex];
                    if (neighborDistance < 0 || neighborDistance >= currentDistance)
                    {
                        continue;
                    }

                    if (neighborDistance < bestDistance)
                    {
                        bestDistance = neighborDistance;
                    }
                }

                if (bestDistance == int.MaxValue)
                {
                    return;
                }

                bool allowLooserPick = rng.NextFloat() < 0.2f;
                int selectedDirection = -1;
                int seen = 0;

                for (int i = 0; i < CardinalDirections.Length; i++)
                {
                    int2 neighbor = current + CardinalDirections[i];
                    if (neighbor.x < 0 || neighbor.y < 0 || neighbor.x >= width || neighbor.y >= height)
                    {
                        continue;
                    }

                    int neighborIndex = neighbor.x + (neighbor.y * width);
                    int neighborDistance = distanceField[neighborIndex];
                    if (neighborDistance < 0 || neighborDistance >= currentDistance)
                    {
                        continue;
                    }

                    int maxAcceptedDistance = allowLooserPick ? (bestDistance + 1) : bestDistance;
                    if (neighborDistance > maxAcceptedDistance)
                    {
                        continue;
                    }

                    seen++;
                    if (rng.NextInt(seen) == 0)
                    {
                        selectedDirection = i;
                    }
                }

                if (selectedDirection < 0)
                {
                    return;
                }

                current += CardinalDirections[selectedDirection];
            }
        }

        private static Unity.Mathematics.Random CreateRandom(int seed, uint salt)
        {
            uint hashed = math.hash(new uint2((uint)seed, salt));
            if (hashed == 0u)
            {
                hashed = 1u;
            }

            return new Unity.Mathematics.Random(hashed);
        }
    }
}
