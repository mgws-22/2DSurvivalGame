using System;
using System.Collections.Generic;
using System.IO;
using Project.Buildings;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Scenes;
using Unity.Scenes.Editor;

namespace Project.Editor.Buildings
{
    public static class WallSubSceneTools
    {
        private const string StaticBuildingsSubSceneName = "StaticBuildingsSubScene";
        private const string CreateSubSceneMenuPath = "Tools/Buildings/Create/Ensure StaticBuildings SubScene";
        private const string MoveWallsMenuPath = "Tools/Buildings/Move Selected Walls To StaticBuildings SubScene";
        private const string ValidateWallBakingMenuPath = "Tools/Buildings/Validate Wall Baking";

        [MenuItem(CreateSubSceneMenuPath)]
        public static void CreateOrEnsureStaticBuildingsSubSceneMenu()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!TryGetActiveScene(activeScene))
            {
                return;
            }

            EnsureStaticBuildingsSubScene(activeScene);
        }

        [MenuItem(MoveWallsMenuPath)]
        public static void MoveSelectedWallsToStaticBuildingsSubScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!TryGetActiveScene(activeScene))
            {
                return;
            }

            SubScene staticBuildingsSubScene = EnsureStaticBuildingsSubScene(activeScene);
            if (staticBuildingsSubScene == null)
            {
                return;
            }

            if (staticBuildingsSubScene.SceneAsset == null)
            {
                Debug.LogError("[WallSubSceneTools] StaticBuildingsSubScene exists but has no SceneAsset assigned.");
                return;
            }

            if (!staticBuildingsSubScene.IsLoaded)
            {
                SubSceneUtility.EditScene(staticBuildingsSubScene);
            }

            Scene subSceneEditingScene = staticBuildingsSubScene.EditingScene;
            if (!subSceneEditingScene.IsValid() || !subSceneEditingScene.isLoaded)
            {
                Debug.LogError("[WallSubSceneTools] Could not open StaticBuildingsSubScene for editing.");
                return;
            }

            List<GameObject> candidates = CollectSelectedWallObjects();
            if (candidates.Count == 0)
            {
                Debug.LogWarning("[WallSubSceneTools] No selected wall GameObjects found. Select objects with WallBuildingAuthoring or names starting with 'Wall'.");
                return;
            }

            int movedCount = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject go = candidates[i];
                if (go == null || EditorUtility.IsPersistent(go))
                {
                    continue;
                }

                if (go == staticBuildingsSubScene.gameObject)
                {
                    continue;
                }

                if (go.scene == subSceneEditingScene)
                {
                    continue;
                }

                if (go.scene != activeScene)
                {
                    Debug.LogWarning("[WallSubSceneTools] Skipped " + go.name + " because it is not in the active scene.");
                    continue;
                }

                if (PrefabUtility.IsPartOfAnyPrefab(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                {
                    Debug.LogWarning("[WallSubSceneTools] Skipped " + go.name + " because it is not an outermost prefab instance root.");
                    continue;
                }

                Transform wallTransform = go.transform;
                wallTransform.SetParent(null, true);
                SceneManager.MoveGameObjectToScene(go, subSceneEditingScene);
                movedCount++;
            }

            if (movedCount == 0)
            {
                Debug.LogWarning("[WallSubSceneTools] No wall GameObjects were moved.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.MarkSceneDirty(subSceneEditingScene);
            EditorSceneManager.SaveScene(subSceneEditingScene);
            EditorSceneManager.SaveScene(activeScene);
            AssetDatabase.SaveAssets();

            Debug.Log("[WallSubSceneTools] Moved " + movedCount + " wall GameObjects to StaticBuildingsSubScene.");
        }

        [MenuItem(ValidateWallBakingMenuPath)]
        public static void ValidateWallBaking()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ValidateWallBaking] Enter Play Mode before running validation.");
                return;
            }

            World defaultWorld = World.DefaultGameObjectInjectionWorld;
            if (defaultWorld == null || !defaultWorld.IsCreated)
            {
                Debug.LogError("[ValidateWallBaking] Default World is not available.");
                return;
            }

            EntityManager entityManager = defaultWorld.EntityManager;
            EntityQuery query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingTag>());
            int count = query.CalculateEntityCount();
            query.Dispose();

            Debug.Log("[ValidateWallBaking] BuildingTag entities: " + count);

            if (count == 0)
            {
                Debug.LogWarning("[ValidateWallBaking] Hint: Put wall GameObjects inside StaticBuildingsSubScene so they bake into the Default World.");
            }
        }

        private static SubScene EnsureStaticBuildingsSubScene(Scene activeScene)
        {
            SubScene existing = FindStaticBuildingsSubScene(activeScene);
            if (existing != null)
            {
                return existing;
            }

            if (string.IsNullOrEmpty(activeScene.path))
            {
                Debug.LogError("[WallSubSceneTools] Save the active scene before creating StaticBuildingsSubScene.");
                return null;
            }

            string subSceneAssetPath = GetStaticBuildingsSubSceneAssetPath(activeScene);
            if (!TryEnsureSubSceneAsset(subSceneAssetPath, activeScene))
            {
                return null;
            }

            SceneAsset subSceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(subSceneAssetPath);
            if (subSceneAsset == null)
            {
                Debug.LogError("[WallSubSceneTools] Failed to load SubScene asset at " + subSceneAssetPath + ".");
                return null;
            }

            SceneManager.SetActiveScene(activeScene);

            GameObject root = new GameObject(StaticBuildingsSubSceneName);
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            SubScene subScene = root.AddComponent<SubScene>();
            subScene.SceneAsset = subSceneAsset;

            EditorUtility.SetDirty(subScene);
            EditorSceneManager.MarkSceneDirty(activeScene);
            AssetDatabase.SaveAssets();

            return subScene;
        }

        private static bool TryEnsureSubSceneAsset(string subSceneAssetPath, Scene activeScene)
        {
            SceneAsset existingAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(subSceneAssetPath);
            if (existingAsset != null)
            {
                return true;
            }

            string directoryPath = Path.GetDirectoryName(subSceneAssetPath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            Scene createdSubScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (!createdSubScene.IsValid())
            {
                Debug.LogError("[WallSubSceneTools] Failed to create an empty scene for StaticBuildingsSubScene.");
                return false;
            }

            try
            {
                SubSceneInspectorUtility.SetSceneAsSubScene(createdSubScene);

                if (!EditorSceneManager.SaveScene(createdSubScene, subSceneAssetPath))
                {
                    Debug.LogError("[WallSubSceneTools] Failed to save StaticBuildingsSubScene asset at " + subSceneAssetPath + ".");
                    return false;
                }
            }
            finally
            {
                if (createdSubScene.IsValid() && createdSubScene.isLoaded)
                {
                    EditorSceneManager.CloseScene(createdSubScene, true);
                }

                if (activeScene.IsValid())
                {
                    SceneManager.SetActiveScene(activeScene);
                }
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        private static string GetStaticBuildingsSubSceneAssetPath(Scene activeScene)
        {
            string activeSceneDirectory = Path.GetDirectoryName(activeScene.path);
            string fileName = activeScene.name + "_" + StaticBuildingsSubSceneName + ".unity";
            string combinedPath = Path.Combine(activeSceneDirectory ?? "Assets", fileName);
            return combinedPath.Replace('\\', '/');
        }

        private static SubScene FindStaticBuildingsSubScene(Scene activeScene)
        {
            GameObject[] roots = activeScene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (!string.Equals(root.name, StaticBuildingsSubSceneName, StringComparison.Ordinal))
                {
                    continue;
                }

                SubScene subScene = root.GetComponent<SubScene>();
                if (subScene != null)
                {
                    return subScene;
                }
            }

            return null;
        }

        private static List<GameObject> CollectSelectedWallObjects()
        {
            GameObject[] selectedObjects = Selection.gameObjects;
            List<GameObject> candidates = new List<GameObject>(selectedObjects.Length);
            HashSet<int> candidateIds = new HashSet<int>();

            for (int i = 0; i < selectedObjects.Length; i++)
            {
                GameObject go = selectedObjects[i];
                if (go == null)
                {
                    continue;
                }

                if (!IsWallCandidate(go))
                {
                    continue;
                }

                candidates.Add(go);
                candidateIds.Add(go.GetInstanceID());
            }

            if (candidates.Count <= 1)
            {
                return candidates;
            }

            List<GameObject> filtered = new List<GameObject>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject go = candidates[i];
                if (HasCandidateAncestor(go.transform.parent, candidateIds))
                {
                    continue;
                }

                filtered.Add(go);
            }

            return filtered;
        }

        private static bool HasCandidateAncestor(Transform parent, HashSet<int> candidateIds)
        {
            while (parent != null)
            {
                if (candidateIds.Contains(parent.gameObject.GetInstanceID()))
                {
                    return true;
                }

                parent = parent.parent;
            }

            return false;
        }

        private static bool IsWallCandidate(GameObject go)
        {
            if (go.GetComponent<WallBuildingAuthoring>() != null)
            {
                return true;
            }

            return go.name.StartsWith("Wall", StringComparison.Ordinal);
        }

        private static bool TryGetActiveScene(Scene activeScene)
        {
            if (!activeScene.IsValid())
            {
                Debug.LogError("[WallSubSceneTools] No active scene is available.");
                return false;
            }

            return true;
        }
    }
}
