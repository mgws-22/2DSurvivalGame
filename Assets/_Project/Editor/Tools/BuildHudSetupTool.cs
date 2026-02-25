using Project.Buildings.Placement;
using Project.UI.BuildMenu;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Project.Editor.Tools
{
    public static class BuildHudSetupTool
    {
        private const string MenuPath = "Tools/Buildings/Setup Build HUD (SampleScene)";
        private const string BuildHudRootName = "Build HUD";
        private const string CanvasName = "Build HUD Canvas";
        private const string PanelName = "BuildMenuPanel";
        private const string HeaderTextName = "BuildMenuHeaderText";
        private const string StatusTextName = "BuildMenuStatusText";
        private const string GridName = "BuildMenuGrid";
        private const string DefaultWallPrefabPath = "Assets/_Project/Prefabs/Buildings/Wall.prefab";

        private static Font s_cachedFont;

        [MenuItem(MenuPath)]
        public static void SetupBuildHud()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[BuildHudSetupTool] No active scene to configure.");
                return;
            }

            EnsureEventSystem();

            GameObject buildHudRoot = GetOrCreateRoot(BuildHudRootName);
            buildHudRoot.transform.position = Vector3.zero;
            buildHudRoot.transform.rotation = Quaternion.identity;
            buildHudRoot.transform.localScale = Vector3.one;

            if (buildHudRoot.GetComponent<BuildMenuController>() == null)
            {
                buildHudRoot.AddComponent<BuildMenuController>();
            }

            if (buildHudRoot.GetComponent<BuildingPlacementController>() == null)
            {
                buildHudRoot.AddComponent<BuildingPlacementController>();
            }

            Canvas canvas = EnsureCanvas(buildHudRoot.transform);
            RectTransform panel = EnsurePanel(canvas.transform);
            EnsureHeaderText(panel);
            EnsureStatusText(panel);
            GridLayoutGroup grid = EnsureGrid(panel);
            EnsureGridButtons(grid.transform);

            EnsureBuildingPrefabCatalogAuthoring();

            EditorSceneManager.MarkSceneDirty(activeScene);
            Debug.Log("[BuildHudSetupTool] Build HUD, placement controllers, EventSystem, and BuildingPrefabCatalogAuthoring are configured.");
        }

        private static void EnsureEventSystem()
        {
            EventSystem eventSystem = Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                GameObject go = new GameObject("EventSystem");
                eventSystem = go.AddComponent<EventSystem>();
                go.AddComponent<InputSystemUIInputModule>();
                return;
            }

            if (eventSystem.GetComponent<InputSystemUIInputModule>() == null &&
                eventSystem.GetComponent<StandaloneInputModule>() == null)
            {
                eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            }
        }

        private static GameObject GetOrCreateRoot(string objectName)
        {
            GameObject root = GameObject.Find(objectName);
            if (root != null)
            {
                return root;
            }

            return new GameObject(objectName);
        }

        private static Canvas EnsureCanvas(Transform parent)
        {
            Transform existing = parent.Find(CanvasName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(CanvasName);
            if (go.transform.parent != parent)
            {
                go.transform.SetParent(parent, false);
            }

            Canvas canvas = go.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = go.AddComponent<Canvas>();
            }

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            if (go.GetComponent<CanvasScaler>() == null)
            {
                go.AddComponent<CanvasScaler>();
            }

            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (go.GetComponent<GraphicRaycaster>() == null)
            {
                go.AddComponent<GraphicRaycaster>();
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;

            return canvas;
        }

        private static RectTransform EnsurePanel(Transform canvasTransform)
        {
            Transform existing = canvasTransform.Find(PanelName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(PanelName);
            if (go.transform.parent != canvasTransform)
            {
                go.transform.SetParent(canvasTransform, false);
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null)
            {
                rt = go.AddComponent<RectTransform>();
            }

            Image image = go.GetComponent<Image>();
            if (image == null)
            {
                image = go.AddComponent<Image>();
            }

            image.color = new Color(0f, 0f, 0f, 0.35f);
            image.raycastTarget = false;

            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(16f, 16f);
            rt.sizeDelta = new Vector2(384f, 280f);
            return rt;
        }

        private static Text EnsureHeaderText(RectTransform panel)
        {
            return EnsureText(
                panel,
                HeaderTextName,
                new Vector2(8f, -8f),
                new Vector2(360f, 20f),
                TextAnchor.UpperLeft,
                14,
                FontStyle.Bold);
        }

        private static Text EnsureStatusText(RectTransform panel)
        {
            Text text = EnsureText(
                panel,
                StatusTextName,
                new Vector2(8f, -32f),
                new Vector2(360f, 32f),
                TextAnchor.UpperLeft,
                12,
                FontStyle.Normal);

            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static GridLayoutGroup EnsureGrid(RectTransform panel)
        {
            Transform existing = panel.Find(GridName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(GridName);
            if (go.transform.parent != panel)
            {
                go.transform.SetParent(panel, false);
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null)
            {
                rt = go.AddComponent<RectTransform>();
            }

            GridLayoutGroup grid = go.GetComponent<GridLayoutGroup>();
            if (grid == null)
            {
                grid = go.AddComponent<GridLayoutGroup>();
            }

            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.cellSize = new Vector2(88f, 44f);
            grid.spacing = new Vector2(4f, 4f);
            grid.padding = new RectOffset(0, 0, 0, 0);
            grid.childAlignment = TextAnchor.UpperLeft;

            float width = (grid.cellSize.x * 4f) + (grid.spacing.x * 3f);
            float height = (grid.cellSize.y * 4f) + (grid.spacing.y * 3f);

            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(8f, 8f);
            rt.sizeDelta = new Vector2(width, height);

            return grid;
        }

        private static void EnsureGridButtons(Transform gridTransform)
        {
            for (int i = 0; i < 16; i++)
            {
                string buttonName = "BuildMenuButton_" + i.ToString("00");
                Transform existing = gridTransform.Find(buttonName);
                GameObject go = existing != null ? existing.gameObject : new GameObject(buttonName);
                if (go.transform.parent != gridTransform)
                {
                    go.transform.SetParent(gridTransform, false);
                }
                go.transform.SetSiblingIndex(i);

                RectTransform rt = go.GetComponent<RectTransform>();
                if (rt == null)
                {
                    rt = go.AddComponent<RectTransform>();
                }

                Image image = go.GetComponent<Image>();
                if (image == null)
                {
                    image = go.AddComponent<Image>();
                }

                image.color = new Color(0.17f, 0.17f, 0.17f, 0.92f);
                image.raycastTarget = true;

                Button button = go.GetComponent<Button>();
                if (button == null)
                {
                    button = go.AddComponent<Button>();
                }

                button.transition = Selectable.Transition.ColorTint;

                Text label = EnsureButtonLabel(go.transform);

                BuildMenuButton buildMenuButton = go.GetComponent<BuildMenuButton>();
                if (buildMenuButton == null)
                {
                    buildMenuButton = go.AddComponent<BuildMenuButton>();
                }

#if UNITY_EDITOR
                buildMenuButton.EditorConfigure(i, button, label);
                EditorUtility.SetDirty(buildMenuButton);
#endif
            }
        }

        private static Text EnsureButtonLabel(Transform buttonTransform)
        {
            const string LabelName = "Label";
            Transform existing = buttonTransform.Find(LabelName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(LabelName);
            if (go.transform.parent != buttonTransform)
            {
                go.transform.SetParent(buttonTransform, false);
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null)
            {
                rt = go.AddComponent<RectTransform>();
            }

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(4f, 2f);
            rt.offsetMax = new Vector2(-4f, -2f);

            Text text = go.GetComponent<Text>();
            if (text == null)
            {
                text = go.AddComponent<Text>();
            }

            text.font = GetBuiltinFont();
            text.text = string.Empty;
            text.fontSize = 11;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 8;
            text.resizeTextMaxSize = 11;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;

            return text;
        }

        private static Text EnsureText(
            RectTransform parent,
            string objectName,
            Vector2 anchoredPosition,
            Vector2 sizeDelta,
            TextAnchor alignment,
            int fontSize,
            FontStyle fontStyle)
        {
            Transform existing = parent.Find(objectName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(objectName);
            if (go.transform.parent != parent)
            {
                go.transform.SetParent(parent, false);
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null)
            {
                rt = go.AddComponent<RectTransform>();
            }

            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;

            Text text = go.GetComponent<Text>();
            if (text == null)
            {
                text = go.AddComponent<Text>();
            }

            text.font = GetBuiltinFont();
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.alignment = alignment;
            text.color = Color.white;
            text.supportRichText = false;
            text.text = string.Empty;
            text.raycastTarget = false;
            return text;
        }

        private static Font GetBuiltinFont()
        {
            if (s_cachedFont != null)
            {
                return s_cachedFont;
            }

            s_cachedFont = TryLoadBuiltinFont("LegacyRuntime.ttf");
            if (s_cachedFont == null)
            {
                s_cachedFont = TryLoadBuiltinFont("Arial.ttf");
            }

            return s_cachedFont;
        }

        private static Font TryLoadBuiltinFont(string fontName)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(fontName);
            }
            catch (System.ArgumentException)
            {
                return null;
            }
        }

        private static void EnsureBuildingPrefabCatalogAuthoring()
        {
            BuildingPrefabCatalogAuthoring authoring = Object.FindFirstObjectByType<BuildingPrefabCatalogAuthoring>();
            if (authoring == null)
            {
                GameObject go = new GameObject("Building Prefab Catalog");
                authoring = go.AddComponent<BuildingPrefabCatalogAuthoring>();
            }

            GameObject wallPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultWallPrefabPath);
            if (wallPrefab != null && authoring.WallPrefab != wallPrefab)
            {
                authoring.WallPrefab = wallPrefab;
                EditorUtility.SetDirty(authoring);
            }
        }
    }
}
