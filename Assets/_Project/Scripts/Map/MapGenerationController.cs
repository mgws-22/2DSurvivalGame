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

        [Header("Debug Gizmos")]
        [SerializeField] private bool _drawSpawnBounds = true;
        [SerializeField] private bool _drawGateGizmos = true;
        [SerializeField] private Color _playBoundsColor = Color.green;
        [SerializeField] private Color _spawnBoundsColor = Color.yellow;
        [SerializeField] private Color _gateColor = Color.cyan;

        private bool _pendingMapEcsSync;

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
            CurrentMap = MapGenerator.Generate(_config, origin);
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
                float2 playSize = new float2(validated.width * validated.tileSize, validated.height * validated.tileSize);
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
                float gateRadiusWorld = math.max(0.1f, validated.gateRadius * validated.tileSize);
                for (int i = 0; i < CurrentMap.GateCount; i++)
                {
                    float2 gateCenter = CurrentMap.GridToWorld(CurrentMap.GetGateCenter(i));
                    Gizmos.DrawWireSphere(new Vector3(gateCenter.x, gateCenter.y, 0f), gateRadiusWorld);
                }
            }
        }
    }
}
