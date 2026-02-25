using Unity.Mathematics;
using UnityEngine;

namespace Project.Map
{
    [DisallowMultipleComponent]
    public sealed class MapGenerationController : MonoBehaviour
    {
        [SerializeField] private MapConfig _config = MapConfig.CreateDefault();
        [SerializeField] private MapTilemapRenderer _tilemapRenderer;
        [SerializeField] private bool _generateOnAwake = true;
        [Header("Runtime Upscaling")]
        [SerializeField, Min(1)] private int _mapScaleFactor = 3;

        [Header("Debug Gizmos")]
        [SerializeField] private bool _drawSpawnBounds = true;
        [SerializeField] private bool _drawGateGizmos = true;
        [SerializeField] private Color _playBoundsColor = Color.green;
        [SerializeField] private Color _spawnBoundsColor = Color.yellow;
        [SerializeField] private Color _gateColor = Color.cyan;

        private bool _pendingMapEcsSync;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private bool _loggedRuntimeScaleInfo;
#endif

        public MapData CurrentMap { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void BootstrapMapController()
        {
            if (UnityEngine.Object.FindFirstObjectByType<MapGenerationController>() != null)
            {
                return;
            }

            GameObject bootstrapObject = new GameObject("Map Generation");
            bootstrapObject.AddComponent<MapGenerationController>();
        }

        private void Reset()
        {
            _config = MapConfig.CreateDefault();
            EnsureRenderer();
        }

        private void Awake()
        {
            if (_config.width <= 0 || _config.height <= 0)
            {
                _config = MapConfig.CreateDefault();
            }

            EnsureRenderer();

            if (_generateOnAwake)
            {
                Regenerate();
            }
        }

        [ContextMenu("Regenerate Map")]
        public void Regenerate()
        {
            _config = _config.GetValidated();
            EnsureRenderer();
            _tilemapRenderer.SetCellSize(_config.tileSize);

            float2 origin = new float2(transform.position.x, transform.position.y);
            int runtimeScaleFactor = Mathf.Max(1, _mapScaleFactor);
            MapData logicalMap = MapGenerator.Generate(_config, origin);
            CurrentMap = ExpandRuntimeMap(logicalMap, runtimeScaleFactor);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogRuntimeScaleInfoOnce(logicalMap, CurrentMap, runtimeScaleFactor);
#endif
            _pendingMapEcsSync = !MapEcsBridge.Sync(CurrentMap);
            _tilemapRenderer.Render(CurrentMap);
        }

        private void LateUpdate()
        {
            if (!_pendingMapEcsSync || CurrentMap == null)
            {
                return;
            }

            _pendingMapEcsSync = !MapEcsBridge.Sync(CurrentMap);
        }

        private void EnsureRenderer()
        {
            if (_tilemapRenderer == null)
            {
                _tilemapRenderer = GetComponent<MapTilemapRenderer>();
            }

            if (_tilemapRenderer == null)
            {
                _tilemapRenderer = gameObject.AddComponent<MapTilemapRenderer>();
            }

            _tilemapRenderer.EnsureTilemap();
        }

        private void OnDrawGizmosSelected()
        {
            MapConfig validated = _config.GetValidated();
            int runtimeScaleFactor = Mathf.Max(1, _mapScaleFactor);

            Bounds playBounds;
            Bounds spawnBounds;
            if (CurrentMap != null)
            {
                playBounds = CurrentMap.GetPlayAreaBoundsWorld();
                spawnBounds = CurrentMap.GetSpawnAreaBoundsWorld();
            }
            else
            {
                float2 origin = new float2(transform.position.x, transform.position.y);
                float2 playSize = new float2(
                    validated.width * runtimeScaleFactor * validated.tileSize,
                    validated.height * runtimeScaleFactor * validated.tileSize);
                float2 playCenter = origin + (playSize * 0.5f);
                playBounds = new Bounds(new Vector3(playCenter.x, playCenter.y, 0f), new Vector3(playSize.x, playSize.y, 0.01f));

                float spawnMarginWorld = validated.spawnMargin * validated.tileSize;
                float2 spawnSize = playSize + (new float2(spawnMarginWorld, spawnMarginWorld) * 2f);
                spawnBounds = new Bounds(new Vector3(playCenter.x, playCenter.y, 0f), new Vector3(spawnSize.x, spawnSize.y, 0.01f));
            }

            Gizmos.color = _playBoundsColor;
            Gizmos.DrawWireCube(playBounds.center, playBounds.size);

            if (_drawSpawnBounds)
            {
                Gizmos.color = _spawnBoundsColor;
                Gizmos.DrawWireCube(spawnBounds.center, spawnBounds.size);
            }

            if (_drawGateGizmos && CurrentMap != null)
            {
                Gizmos.color = _gateColor;
                float gateRadiusWorld = math.max(0.1f, validated.gateRadius * runtimeScaleFactor * validated.tileSize);
                for (int i = 0; i < CurrentMap.GateCount; i++)
                {
                    float2 gateCenter = CurrentMap.GridToWorld(CurrentMap.GetGateCenter(i));
                    Gizmos.DrawWireSphere(new Vector3(gateCenter.x, gateCenter.y, 0f), gateRadiusWorld);
                }
            }
        }

        private static MapData ExpandRuntimeMap(MapData logicalMap, int scaleFactor)
        {
            if (logicalMap == null || scaleFactor <= 1)
            {
                return logicalMap;
            }

            int logicalWidth = logicalMap.Width;
            int logicalHeight = logicalMap.Height;
            int runtimeWidth = logicalWidth * scaleFactor;
            int runtimeHeight = logicalHeight * scaleFactor;

            bool[] runtimeWalkable = new bool[runtimeWidth * runtimeHeight];

            for (int y = 0; y < logicalHeight; y++)
            {
                int runtimeBaseY = y * scaleFactor;
                for (int x = 0; x < logicalWidth; x++)
                {
                    bool isWalkable = logicalMap.IsWalkable(x, y);
                    int runtimeBaseX = x * scaleFactor;

                    for (int dy = 0; dy < scaleFactor; dy++)
                    {
                        int runtimeRowOffset = (runtimeBaseY + dy) * runtimeWidth;
                        int runtimeIndex = runtimeRowOffset + runtimeBaseX;
                        for (int dx = 0; dx < scaleFactor; dx++)
                        {
                            runtimeWalkable[runtimeIndex + dx] = isWalkable;
                        }
                    }
                }
            }

            int gateCount = logicalMap.GateCount;
            int2[] runtimeGateCenters = new int2[gateCount];
            int gateCenterOffset = (scaleFactor - 1) / 2;
            for (int i = 0; i < gateCount; i++)
            {
                int2 logicalGate = logicalMap.GetGateCenter(i);
                runtimeGateCenters[i] = new int2(
                    (logicalGate.x * scaleFactor) + gateCenterOffset,
                    (logicalGate.y * scaleFactor) + gateCenterOffset);
            }

            MapData runtimeMap = new MapData(
                runtimeWidth,
                runtimeHeight,
                logicalMap.TileSize,
                logicalMap.SpawnMargin, // Intentionally not scaled; scale here later if wider spawn ring is desired.
                logicalMap.CenterOpenRadius * scaleFactor,
                logicalMap.WorldOrigin,
                runtimeGateCenters);

            runtimeMap.FillFromWalkable(runtimeWalkable);
            return runtimeMap;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void LogRuntimeScaleInfoOnce(MapData logicalMap, MapData runtimeMap, int scaleFactor)
        {
            if (_loggedRuntimeScaleInfo || logicalMap == null || runtimeMap == null)
            {
                return;
            }

            _loggedRuntimeScaleInfo = true;
            UnityEngine.Debug.Log(
                "Map runtime upscale: logical="
                + logicalMap.Width + "x" + logicalMap.Height
                + ", runtime=" + runtimeMap.Width + "x" + runtimeMap.Height
                + ", scaleFactor=" + scaleFactor + ".");
        }
#endif
    }
}
