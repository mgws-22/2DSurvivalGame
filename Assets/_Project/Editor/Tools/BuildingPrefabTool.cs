using System.IO;
using Project.Buildings;
using UnityEditor;
using UnityEngine;

namespace Project.Editor.Tools
{
    public static class BuildingPrefabTool
    {
        private const string BuildingsArtFolder = "Assets/_Project/Art/Generated/Buildings";
        private const string PrefabFolder = "Assets/_Project/Prefabs/Buildings";
        private const string WallSpritePath = BuildingsArtFolder + "/WallPlaceholder.png";
        private const string WallPrefabPath = PrefabFolder + "/Wall.prefab";

        [MenuItem("Tools/Buildings/Create Wall Prefab")]
        public static void CreateWallPrefab()
        {
            EnsureFolderTree(BuildingsArtFolder);
            EnsureFolderTree(PrefabFolder);

            Sprite sprite = EnsureWallPlaceholderSprite();

            GameObject root = new GameObject("Wall");
            try
            {
                SpriteRenderer spriteRenderer = root.AddComponent<SpriteRenderer>();
                spriteRenderer.sprite = sprite;

                root.AddComponent<WallBuildingAuthoring>();

                PrefabUtility.SaveAsPrefabAsset(root, WallPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static Sprite EnsureWallPlaceholderSprite()
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(WallSpritePath);
            if (sprite != null)
            {
                return sprite;
            }

            Texture2D texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color32 fill = new Color32(94, 94, 94, 255);
            Color32 border = new Color32(196, 196, 196, 255);
            Color32 shadow = new Color32(54, 54, 54, 255);

            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    bool isBorder = x == 0 || y == 0 || x == 15 || y == 15;
                    bool isInnerShadow = x == 1 || y == 1 || x == 14 || y == 14;
                    Color32 color = fill;
                    if (isBorder)
                    {
                        color = border;
                    }
                    else if (isInnerShadow)
                    {
                        color = shadow;
                    }

                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply(false, false);
            byte[] pngBytes = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            File.WriteAllBytes(WallSpritePath, pngBytes);
            AssetDatabase.ImportAsset(WallSpritePath, ImportAssetOptions.ForceSynchronousImport);

            TextureImporter importer = AssetImporter.GetAtPath(WallSpritePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = 16f;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Point;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(WallSpritePath);
        }

        private static void EnsureFolderTree(string assetPath)
        {
            string[] parts = assetPath.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                return;
            }

            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
    }
}
