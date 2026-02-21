using UnityEngine;

public class FpsCap : MonoBehaviour
{
    [Range(10, 500)] public int targetFps = 60;
    public bool disableVSync = true;

    void Awake()
    {
        if (disableVSync) QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = targetFps;
        Debug.Log($"[FpsCap] targetFrameRate={Application.targetFrameRate} vSyncCount={QualitySettings.vSyncCount}");
    }
}