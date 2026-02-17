using Project.Horde;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Project.Editor.Tools
{
    public static class SurvivalSceneSetupTool
    {
        private const string ZombiePrefabPath = "Assets/GameObject.prefab";

        [MenuItem("Tools/Survival/Setup Zombie Demo Scene")]
        public static void SetupZombieDemoScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[SurvivalSceneSetupTool] No active scene to configure.");
                return;
            }

            ZombieSpawnConfigAuthoring configAuthoring = Object.FindFirstObjectByType<ZombieSpawnConfigAuthoring>();
            if (configAuthoring == null)
            {
                GameObject go = new GameObject("Zombie Spawn Config");
                configAuthoring = go.AddComponent<ZombieSpawnConfigAuthoring>();
            }

            SerializedObject serializedObject = new SerializedObject(configAuthoring);
            GameObject zombiePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ZombiePrefabPath);

            serializedObject.FindProperty("_zombiePrefab").objectReferenceValue = zombiePrefab;
            serializedObject.FindProperty("_spawnRate").floatValue = 1f;
            serializedObject.FindProperty("_spawnBatchSize").intValue = 55;
            serializedObject.FindProperty("_maxAlive").intValue = 256;
            serializedObject.FindProperty("_seed").intValue = 12345;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(configAuthoring);
            EditorSceneManager.MarkSceneDirty(activeScene);

            if (zombiePrefab == null)
            {
                Debug.LogWarning("[SurvivalSceneSetupTool] Config created, but prefab at Assets/GameObject.prefab was not found. Assign _zombiePrefab manually.");
                return;
            }

            Debug.Log("[SurvivalSceneSetupTool] Zombie demo scene configured. ZombieSpawnConfigAuthoring is present and prefab/default values are assigned.");
        }
    }
}
