#if UNITY_EDITOR
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Project.Map.Debug
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class FlowFieldGizmosDrawer : MonoBehaviour
    {
        private const byte NoneDirection = 255;

        [Header("Draw Toggles")]
        public bool draw = true;
        public bool drawOnlyWhenSelected;
        public bool drawBounds = true;
        public bool drawCenter = true;
        public bool drawUnreachableCross = false;

        [Header("Sampling")]
        [Min(1)] public int sampleStep = 4;
        [Min(0.01f)] public float arrowScale = 0.35f;
        public float yOffset;

        [Header("Colors")]
        public Color arrowColor = new Color(0.2f, 0.9f, 1f, 1f);
        public Color unreachableColor = new Color(1f, 0.35f, 0.35f, 0.9f);
        public Color boundsColor = new Color(0.4f, 1f, 0.4f, 0.8f);
        public Color centerColor = new Color(1f, 0.85f, 0.2f, 0.8f);

        private World _cachedWorld;
        private EntityQuery _flowQuery;
        private EntityQuery _mapQuery;
        private bool _queriesInitialized;

        private static readonly float2[] DirLut =
        {
            new float2(0f, 1f),  // N
            new float2(1f, 0f),  // E
            new float2(0f, -1f), // S
            new float2(-1f, 0f)  // W
        };

        private void OnDisable()
        {
            DisposeQueries();
        }

        private void OnDrawGizmos()
        {
            if (drawOnlyWhenSelected)
            {
                return;
            }

            DrawGizmosInternal();
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawOnlyWhenSelected)
            {
                return;
            }

            DrawGizmosInternal();
        }

        private void DrawGizmosInternal()
        {
            if (!draw)
            {
                return;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                DisposeQueries();
                return;
            }

            if (!EnsureQueries(world))
            {
                return;
            }

            EntityManager entityManager = world.EntityManager;
            if (_flowQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            FlowFieldSingleton flowSingleton = entityManager.GetComponentData<FlowFieldSingleton>(_flowQuery.GetSingletonEntity());
            if (!flowSingleton.Blob.IsCreated)
            {
                return;
            }

            ref FlowFieldBlob flow = ref flowSingleton.Blob.Value;
            int width = flow.Width;
            int height = flow.Height;
            int step = math.max(1, sampleStep);
            float cellSize = flow.CellSize;
            float half = cellSize * 0.5f;
            float2 origin = flow.OriginWorld;
            float z = yOffset;

            if (drawBounds)
            {
                Gizmos.color = boundsColor;
                float3 boundsCenter = new float3(origin.x + (width * cellSize * 0.5f), origin.y + (height * cellSize * 0.5f), z);
                float3 boundsSize = new float3(width * cellSize, height * cellSize, 0.01f);
                Gizmos.DrawWireCube(boundsCenter, boundsSize);
            }

            if (drawCenter && !_mapQuery.IsEmptyIgnoreFilter)
            {
                MapRuntimeData mapData = entityManager.GetComponentData<MapRuntimeData>(_mapQuery.GetSingletonEntity());
                Gizmos.color = centerColor;
                float radiusWorld = math.max(0.1f, mapData.CenterOpenRadius * mapData.TileSize);
                Gizmos.DrawWireSphere(new float3(mapData.CenterWorld.x, mapData.CenterWorld.y, z), radiusWorld);
            }

            for (int y = 0; y < height; y += step)
            {
                int row = y * width;
                for (int x = 0; x < width; x += step)
                {
                    int index = row + x;
                    byte dir = flow.Dir[index];
                    float2 center2 = origin + ((new float2(x + 0.5f, y + 0.5f)) * cellSize);
                    float3 center3 = new float3(center2.x, center2.y, z);

                    if (dir == NoneDirection || dir >= DirLut.Length)
                    {
                        if (drawUnreachableCross)
                        {
                            DrawCross(center3, half * 0.3f);
                        }

                        continue;
                    }

                    float2 d = DirLut[dir];
                    DrawArrow(center3, d, arrowScale);
                }
            }
        }

        private bool EnsureQueries(World world)
        {
            if (_queriesInitialized && _cachedWorld == world && _cachedWorld.IsCreated)
            {
                return true;
            }

            DisposeQueries();
            _cachedWorld = world;

            EntityManager entityManager = world.EntityManager;
            _flowQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<FlowFieldSingleton>());
            _mapQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<MapRuntimeData>());
            _queriesInitialized = true;
            return true;
        }

        private void DisposeQueries()
        {
            if (_queriesInitialized)
            {
                _flowQuery.Dispose();
                _mapQuery.Dispose();
                _queriesInitialized = false;
            }

            _cachedWorld = null;
        }

        private void DrawArrow(float3 origin, float2 dir, float len)
        {
            float3 tip = origin + new float3(dir.x * len, dir.y * len, 0f);
            Gizmos.color = arrowColor;
            Gizmos.DrawLine(origin, tip);

            float2 left2 = new float2(-dir.y, dir.x);
            float3 leftHead = tip + new float3((-dir.x + left2.x) * (len * 0.35f), (-dir.y + left2.y) * (len * 0.35f), 0f);
            float3 rightHead = tip + new float3((-dir.x - left2.x) * (len * 0.35f), (-dir.y - left2.y) * (len * 0.35f), 0f);
            Gizmos.DrawLine(tip, leftHead);
            Gizmos.DrawLine(tip, rightHead);
        }

        private void DrawCross(float3 center, float size)
        {
            Gizmos.color = unreachableColor;
            float3 a = new float3(center.x - size, center.y - size, center.z);
            float3 b = new float3(center.x + size, center.y + size, center.z);
            float3 c = new float3(center.x - size, center.y + size, center.z);
            float3 d = new float3(center.x + size, center.y - size, center.z);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(c, d);
        }
    }
}
#endif
