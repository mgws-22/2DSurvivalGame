using Unity.Mathematics;
using UnityEngine;

namespace Project.Map
{
    public static class CliffTileTextureFactory
    {
        public static Texture2D CreateGroundTexture(int size, Color groundColor, Color accentColor, string textureName)
        {
            Texture2D texture = CreateTexture(size, textureName);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float noise = ((x * 17) + (y * 31)) % 23 / 22f;
                    float blend = 0.12f + (noise * 0.16f);
                    texture.SetPixel(x, y, Color.Lerp(groundColor, accentColor, blend));
                }
            }

            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return texture;
        }

        public static Texture2D CreateCliffMaskTexture(
            int mask,
            int size,
            int edgeThickness,
            Color cliffColor,
            Color edgeColor,
            Color accentColor,
            string textureName)
        {
            Texture2D texture = CreateTexture(size, textureName);
            int thickness = math.clamp(edgeThickness, 1, math.max(1, size / 2));

            bool north = (mask & 0x1) != 0;
            bool east = (mask & 0x2) != 0;
            bool south = (mask & 0x4) != 0;
            bool west = (mask & 0x8) != 0;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float baseNoise = ((x * 13) + (y * 7) + (mask * 19)) % 19 / 18f;
                    Color color = Color.Lerp(cliffColor, accentColor, baseNoise * 0.18f);

                    float edgeWeight = 0f;
                    if (north)
                    {
                        edgeWeight = math.max(edgeWeight, 1f - ((size - 1 - y) / (float)thickness));
                    }

                    if (east)
                    {
                        edgeWeight = math.max(edgeWeight, 1f - ((size - 1 - x) / (float)thickness));
                    }

                    if (south)
                    {
                        edgeWeight = math.max(edgeWeight, 1f - (y / (float)thickness));
                    }

                    if (west)
                    {
                        edgeWeight = math.max(edgeWeight, 1f - (x / (float)thickness));
                    }

                    edgeWeight = math.saturate(edgeWeight);
                    if (edgeWeight > 0f)
                    {
                        color = Color.Lerp(color, edgeColor, edgeWeight);
                    }

                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return texture;
        }

        private static Texture2D CreateTexture(int size, string textureName)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = textureName
            };
            return texture;
        }
    }
}
