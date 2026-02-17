using System.IO;
using Project.Map;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Project.Editor.Tools
{
    public static class MapPlaceholderCliffTileGenerator
    {
        private const string RootFolder = "Assets/_Project/Art/Generated/Tiles/Cliff";
        private const string ResourcesFolder = RootFolder + "/Resources";
        private const string ResourcesMapFolder = ResourcesFolder + "/Map";

        private const string GroundTexturePath = RootFolder + "/Ground.png";
        private const string GroundTilePath = RootFolder + "/Ground.asset";

        private const string TileSetPath = ResourcesMapFolder + "/CliffTileSet.asset";

        private const int TextureSize = 16;
        private const int EdgeThickness = 3;

        private static readonly Color GroundColor = new Color(0.28f, 0.42f, 0.2f, 1f);
        private static readonly Color GroundAccent = new Color(0.34f, 0.5f, 0.25f, 1f);
        private static readonly Color CliffColor = new Color(0.2f, 0.2f, 0.22f, 1f);
        private static readonly Color CliffAccent = new Color(0.26f, 0.26f, 0.28f, 1f);
        private static readonly Color CliffEdgeColor = new Color(0.62f, 0.62f, 0.66f, 1f);

        [MenuItem("Tools/Map/Generate Placeholder Cliff Tiles")]
        public static void Generate()
        {
            EnsureFolderHierarchy();

            Sprite groundSprite = CreateOrReplaceSprite(
                GroundTexturePath,
                CliffTileTextureFactory.CreateGroundTexture(TextureSize, GroundColor, GroundAccent, "GroundTexture"));

            Tile groundTile = CreateOrUpdateTile(GroundTilePath, "Ground", groundSprite);
            TileBase[] cliffTiles = new TileBase[16];

            for (int mask = 0; mask < 16; mask++)
            {
                string maskLabel = mask.ToString("X1");
                string texturePath = RootFolder + "/CliffMask_" + maskLabel + ".png";
                string tilePath = RootFolder + "/CliffMask_" + maskLabel + ".asset";

                Texture2D texture = CliffTileTextureFactory.CreateCliffMaskTexture(
                    mask,
                    TextureSize,
                    EdgeThickness,
                    CliffColor,
                    CliffEdgeColor,
                    CliffAccent,
                    "CliffMaskTexture_" + maskLabel);

                Sprite sprite = CreateOrReplaceSprite(texturePath, texture);
                cliffTiles[mask] = CreateOrUpdateTile(tilePath, "CliffMask_" + maskLabel, sprite);
            }

            CliffTileSet tileSet = LoadOrCreateTileSet(TileSetPath);
            tileSet.SetTiles(groundTile, cliffTiles);
            EditorUtility.SetDirty(tileSet);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("Generated placeholder cliff autotile assets at " + RootFolder);
        }

        private static void EnsureFolderHierarchy()
        {
            EnsureFolder("Assets/_Project/Art");
            EnsureFolder("Assets/_Project/Art/Generated");
            EnsureFolder("Assets/_Project/Art/Generated/Tiles");
            EnsureFolder(RootFolder);
            EnsureFolder(ResourcesFolder);
            EnsureFolder(ResourcesMapFolder);
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            int separator = folderPath.LastIndexOf('/');
            string parent = folderPath.Substring(0, separator);
            string name = folderPath.Substring(separator + 1);

            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, name);
        }

        private static Sprite CreateOrReplaceSprite(string texturePath, Texture2D texture)
        {
            try
            {
                string absolutePath = Path.GetFullPath(texturePath);
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);
            ConfigureSpriteImporter(texturePath);
            return AssetDatabase.LoadAssetAtPath<Sprite>(texturePath);
        }

        private static void ConfigureSpriteImporter(string texturePath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = TextureSize;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = true;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        private static Tile CreateOrUpdateTile(string assetPath, string assetName, Sprite sprite)
        {
            Tile tile = AssetDatabase.LoadAssetAtPath<Tile>(assetPath);
            if (tile == null)
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.name = assetName;
                AssetDatabase.CreateAsset(tile, assetPath);
            }

            tile.sprite = sprite;
            tile.colliderType = Tile.ColliderType.None;
            EditorUtility.SetDirty(tile);
            return tile;
        }

        private static CliffTileSet LoadOrCreateTileSet(string assetPath)
        {
            CliffTileSet tileSet = AssetDatabase.LoadAssetAtPath<CliffTileSet>(assetPath);
            if (tileSet != null)
            {
                return tileSet;
            }

            tileSet = ScriptableObject.CreateInstance<CliffTileSet>();
            tileSet.name = "CliffTileSet";
            AssetDatabase.CreateAsset(tileSet, assetPath);
            return tileSet;
        }
    }
}
