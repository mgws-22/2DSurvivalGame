using System.Threading;
using Unity.Collections;

namespace Project.Horde.Unsafe
{
    public static class AtomicFloat
    {
        // Unsafe-free fixed-point atomic accumulation helper.
        // Store corrections in NativeArray<int> and convert with Scale when reading/writing.
        public const int Scale = 10000;

        public static int ToFixed(float value)
        {
            return (int)System.MathF.Round(value * Scale);
        }

        public static float FromFixed(int value)
        {
            return value / (float)Scale;
        }

        public static void Add(NativeArray<int> values, int index, float delta)
        {
            values[index] += ToFixed(delta);
        }
    }
}
