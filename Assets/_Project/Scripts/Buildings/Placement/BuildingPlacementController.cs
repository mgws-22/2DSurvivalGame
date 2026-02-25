using System;
using System.Collections.Generic;
using Project.Buildings;
using Project.Map;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Project.Buildings.Placement
{
    [DisallowMultipleComponent]
    public sealed class BuildingPlacementController : MonoBehaviour
    {
        private const float FocusPlaneZ = 0f;
        private const string GhostObjectName = "Wall Placement Ghost";

        private const string StatusPlacingOk = "Placing: Wall (LMB place, RMB cancel)";
        private const string StatusNoMapData = "Cannot place: No map data";
        private const string StatusOutOfBounds = "Cannot place: Out of bounds";
        private const string StatusNotWalkableBase = "Cannot place: Base not walkable";
        private const string StatusOccupied = "Cannot place: Cell occupied";
        private const string StatusNoCatalog = "Cannot place: No catalog";
        private const string StatusWallPrefabNull = "Cannot place: Wall prefab missing";
        private const string StatusNoRequestBuffer = "Cannot place: No request buffer";

        private static readonly Color GhostValidColor = new Color(0.25f, 1f, 0.25f, 0.5f);
        private static readonly Color GhostInvalidColor = new Color(1f, 0.25f, 0.25f, 0.5f);
        private static readonly List<RaycastResult> s_uiHits = new List<RaycastResult>(16);

        private enum PlacementValidity : byte
        {
            NoMapData = 0,
            OutOfBounds = 1,
            NotWalkableBase = 2,
            OccupiedByObstacleRect = 3,
            NoCatalog = 4,
            WallPrefabNull = 5,
            NoRequestBuffer = 6,
            OK = 7
        }

        private World _cachedWorld;
        private EntityQuery _mapQuery;
        private EntityQuery _requestQueueQuery;
        private EntityQuery _obstacleRegistryQuery;
        private EntityQuery _catalogQuery;
        private bool _queriesInitialized;

        private Camera _camera;
        private GameObject _ghostObject;
        private SpriteRenderer _ghostRenderer;
        private Sprite _ghostSprite;

        private EventSystem _cachedEventSystem;
        private PointerEventData _pointerEventData;

        private bool _isPlacingWall;
        private bool _hasSnappedCell;
        private int2 _snappedCell;
        private float3 _snappedWorld;
        private float _ghostTileSize = -1f;
        private PlacementValidity _currentValidity = PlacementValidity.NoMapData;
        private string _currentPlacementStatusText = string.Empty;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private bool _loggedRuntimeValidationOnce;
#endif

        public event Action<bool> WallPlacementModeChanged;
        public event Action<string> PlacementStatusChanged;

        public bool IsPlacingWall => _isPlacingWall;
        public string CurrentPlacementStatusText => _currentPlacementStatusText;

        private void Awake()
        {
            _camera = Camera.main;
            EnsureGhostObject();
            SetGhostVisible(false);
            SetPlacementStatusText(string.Empty);
        }

        private void OnDisable()
        {
            if (_isPlacingWall)
            {
                CancelPlacement();
            }
            else
            {
                SetGhostVisible(false);
                SetPlacementStatusText(string.Empty);
            }
        }

        private void OnDestroy()
        {
            DisposeQueries();

            if (_ghostObject != null)
            {
                Destroy(_ghostObject);
            }

            if (_ghostSprite != null)
            {
                Destroy(_ghostSprite);
            }
        }

        private void Update()
        {
            if (!_isPlacingWall)
            {
                return;
            }

            if (RightClickDownThisFrame())
            {
                CancelPlacement();
                return;
            }

            PlacementValidity validity = EvaluatePlacementValidity(
                out EntityManager entityManager,
                out Entity mapEntity,
                out MapRuntimeData map,
                out Entity requestQueueEntity);

            _currentValidity = validity;
            ApplyGhostVisual(validity);
            SetPlacementStatusText(StatusForValidity(validity));

            if (!LeftClickDownThisFrame())
            {
                return;
            }

            if (IsPointerOverUi())
            {
                return;
            }

            if (validity != PlacementValidity.OK)
            {
                return;
            }

            TryQueueWallPlacement(entityManager, mapEntity, map, requestQueueEntity);
        }

        public void BeginWallPlacement()
        {
            if (_isPlacingWall)
            {
                return;
            }

            _isPlacingWall = true;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            TryLogRuntimePlacementValidationOnce();
#endif

            WallPlacementModeChanged?.Invoke(true);
        }

        public void CancelPlacement()
        {
            if (!_isPlacingWall)
            {
                return;
            }

            _isPlacingWall = false;
            _hasSnappedCell = false;
            _currentValidity = PlacementValidity.NoMapData;
            SetGhostVisible(false);
            SetPlacementStatusText(string.Empty);
            WallPlacementModeChanged?.Invoke(false);
        }

        private PlacementValidity EvaluatePlacementValidity(
            out EntityManager entityManager,
            out Entity mapEntity,
            out MapRuntimeData map,
            out Entity requestQueueEntity)
        {
            entityManager = default;
            mapEntity = Entity.Null;
            map = default;
            requestQueueEntity = Entity.Null;

            if (!TryGetMap(out entityManager, out mapEntity, out map))
            {
                _hasSnappedCell = false;
                SetGhostVisible(false);
                return PlacementValidity.NoMapData;
            }

            if (!UpdateSnappedGhostPosition(map))
            {
                _hasSnappedCell = false;
                SetGhostVisible(false);
                return PlacementValidity.NoMapData;
            }

            if (!_hasSnappedCell)
            {
                return PlacementValidity.NoMapData;
            }

            if (!map.IsInMap(_snappedCell))
            {
                return PlacementValidity.OutOfBounds;
            }

            if (!TryGetCatalog(entityManager, out BuildingPrefabCatalog catalog))
            {
                return PlacementValidity.NoCatalog;
            }

            if (!IsValidWallPrefab(entityManager, catalog.WallPrefab))
            {
                return PlacementValidity.WallPrefabNull;
            }

            if (!TryGetRequestQueueEntity(entityManager, out requestQueueEntity))
            {
                return PlacementValidity.NoRequestBuffer;
            }

            if (!IsBaseGroundWalkable(entityManager, mapEntity, map, _snappedCell))
            {
                return PlacementValidity.NotWalkableBase;
            }

            if (IsInsideExistingDynamicObstacle(entityManager, _snappedCell))
            {
                return PlacementValidity.OccupiedByObstacleRect;
            }

            return PlacementValidity.OK;
        }

        private bool UpdateSnappedGhostPosition(MapRuntimeData map)
        {
            if (!TryGetMouseWorld(out float3 mouseWorld))
            {
                return false;
            }

            _snappedCell = map.WorldToGrid(mouseWorld.xy);
            _snappedWorld = map.GridToWorld(_snappedCell, FocusPlaneZ);
            _hasSnappedCell = true;

            EnsureGhostObject();
            if (_ghostObject == null)
            {
                return false;
            }

            if (!Mathf.Approximately(_ghostTileSize, map.TileSize))
            {
                _ghostTileSize = map.TileSize;
                _ghostObject.transform.localScale = new Vector3(_ghostTileSize, _ghostTileSize, 1f);
            }

            _ghostObject.transform.position = new Vector3(_snappedWorld.x, _snappedWorld.y, _snappedWorld.z);
            SetGhostVisible(true);
            return true;
        }

        private void ApplyGhostVisual(PlacementValidity validity)
        {
            if (_ghostRenderer == null || !_ghostRenderer.enabled)
            {
                return;
            }

            _ghostRenderer.color = validity == PlacementValidity.OK ? GhostValidColor : GhostInvalidColor;
        }

        private void TryQueueWallPlacement(EntityManager entityManager, Entity mapEntity, MapRuntimeData map, Entity queueEntity)
        {
            if (!_hasSnappedCell)
            {
                return;
            }

            if (!entityManager.Exists(queueEntity))
            {
                return;
            }

            DynamicBuffer<PlaceBuildingRequest> requests = entityManager.GetBuffer<PlaceBuildingRequest>(queueEntity);
            if (HasPendingWallRequestForCell(requests, _snappedCell))
            {
                return;
            }

            // Re-validate critical checks on click using latest ECS state to avoid racey false positives.
            if (!map.IsInMap(_snappedCell))
            {
                return;
            }

            if (!IsBaseGroundWalkable(entityManager, mapEntity, map, _snappedCell))
            {
                return;
            }

            if (IsInsideExistingDynamicObstacle(entityManager, _snappedCell))
            {
                return;
            }

            requests.Add(new PlaceBuildingRequest
            {
                Type = (byte)BuildingPlacementType.Wall,
                Cell = _snappedCell,
                WorldPos = _snappedWorld
            });
        }

        private bool IsBaseGroundWalkable(EntityManager entityManager, Entity mapEntity, MapRuntimeData map, int2 cell)
        {
            if (!map.IsInMap(cell))
            {
                return false;
            }

            int index = map.ToIndex(cell);
            if (index < 0)
            {
                return false;
            }

            DynamicBuffer<MapWalkableCell> walkable = entityManager.GetBuffer<MapWalkableCell>(mapEntity);
            if (index >= walkable.Length)
            {
                return false;
            }

            return walkable[index].IsWalkable;
        }

        private bool IsInsideExistingDynamicObstacle(EntityManager entityManager, int2 cell)
        {
            if (_obstacleRegistryQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            Entity registryEntity = _obstacleRegistryQuery.GetSingletonEntity();
            DynamicBuffer<DynamicObstacleRect> obstacleRects = entityManager.GetBuffer<DynamicObstacleRect>(registryEntity);
            for (int i = 0; i < obstacleRects.Length; i++)
            {
                DynamicObstacleRect rect = obstacleRects[i];
                if (cell.x < rect.MinCell.x || cell.y < rect.MinCell.y)
                {
                    continue;
                }

                if (cell.x >= rect.MaxCellExclusive.x || cell.y >= rect.MaxCellExclusive.y)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool HasPendingWallRequestForCell(DynamicBuffer<PlaceBuildingRequest> requests, int2 cell)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                PlaceBuildingRequest request = requests[i];
                if (request.Type != (byte)BuildingPlacementType.Wall)
                {
                    continue;
                }

                if (request.Cell.x == cell.x && request.Cell.y == cell.y)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetMap(out EntityManager entityManager, out Entity mapEntity, out MapRuntimeData map)
        {
            entityManager = default;
            mapEntity = Entity.Null;
            map = default;

            if (!EnsureQueries())
            {
                return false;
            }

            if (_mapQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            entityManager = _cachedWorld.EntityManager;
            mapEntity = _mapQuery.GetSingletonEntity();
            map = entityManager.GetComponentData<MapRuntimeData>(mapEntity);
            return true;
        }

        private bool TryGetCatalog(EntityManager entityManager, out BuildingPrefabCatalog catalog)
        {
            catalog = default;

            if (!EnsureQueries())
            {
                return false;
            }

            if (_catalogQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            Entity catalogEntity = _catalogQuery.GetSingletonEntity();
            if (!entityManager.Exists(catalogEntity))
            {
                return false;
            }

            catalog = entityManager.GetComponentData<BuildingPrefabCatalog>(catalogEntity);
            return true;
        }

        private bool TryGetRequestQueueEntity(EntityManager entityManager, out Entity queueEntity)
        {
            queueEntity = Entity.Null;

            if (!EnsureQueries())
            {
                return false;
            }

            if (_requestQueueQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            queueEntity = _requestQueueQuery.GetSingletonEntity();
            if (!entityManager.Exists(queueEntity))
            {
                return false;
            }

            return entityManager.HasBuffer<PlaceBuildingRequest>(queueEntity);
        }

        private static bool IsValidWallPrefab(EntityManager entityManager, Entity wallPrefab)
        {
            if (wallPrefab == Entity.Null || !entityManager.Exists(wallPrefab))
            {
                return false;
            }

            return entityManager.HasComponent<Prefab>(wallPrefab) &&
                   entityManager.HasComponent<BuildingTag>(wallPrefab) &&
                   entityManager.HasComponent<BuildingFootprint>(wallPrefab);
        }

        private bool EnsureQueries()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                DisposeQueries();
                return false;
            }

            if (_queriesInitialized && _cachedWorld == world && _cachedWorld.IsCreated)
            {
                return true;
            }

            DisposeQueries();
            _cachedWorld = world;

            EntityManager entityManager = world.EntityManager;
            _mapQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<MapRuntimeData>(),
                ComponentType.ReadOnly<MapWalkableCell>());
            _requestQueueQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PlaceBuildingRequestQueueTag>(),
                ComponentType.ReadWrite<PlaceBuildingRequest>());
            _obstacleRegistryQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<DynamicObstacleRegistryTag>(),
                ComponentType.ReadOnly<DynamicObstacleRect>());
            _catalogQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingPrefabCatalog>());

            _queriesInitialized = true;
            return true;
        }

        private void DisposeQueries()
        {
            if (!_queriesInitialized)
            {
                _cachedWorld = null;
                return;
            }

            try
            {
                _mapQuery.Dispose();
            }
            catch
            {
            }

            try
            {
                _requestQueueQuery.Dispose();
            }
            catch
            {
            }

            try
            {
                _obstacleRegistryQuery.Dispose();
            }
            catch
            {
            }

            try
            {
                _catalogQuery.Dispose();
            }
            catch
            {
            }

            _queriesInitialized = false;
            _cachedWorld = null;
        }

        private bool TryGetMouseWorld(out float3 worldPos)
        {
            worldPos = default;

            if (_camera == null)
            {
                _camera = Camera.main;
            }

            if (_camera == null)
            {
                return false;
            }

            if (!TryGetMouseScreenPosition(out Vector2 mousePosition))
            {
                return false;
            }

            float planeDistance = Mathf.Abs(_camera.transform.position.z - FocusPlaneZ);
            Vector3 world = _camera.ScreenToWorldPoint(new Vector3(mousePosition.x, mousePosition.y, planeDistance));
            worldPos = new float3(world.x, world.y, FocusPlaneZ);
            return true;
        }

        private static bool TryGetMouseScreenPosition(out Vector2 screenPosition)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                screenPosition = mouse.position.ReadValue();
                return true;
            }

            Vector3 legacyMouse = Input.mousePosition;
            screenPosition = new Vector2(legacyMouse.x, legacyMouse.y);
            return true;
        }

        private static bool LeftClickDownThisFrame()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                return mouse.leftButton.wasPressedThisFrame;
            }

            return Input.GetMouseButtonDown(0);
        }

        private static bool RightClickDownThisFrame()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                return mouse.rightButton.wasPressedThisFrame;
            }

            return Input.GetMouseButtonDown(1);
        }

        private bool IsPointerOverUi()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return false;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return false;
            }

            if (_pointerEventData == null || _cachedEventSystem != eventSystem)
            {
                _cachedEventSystem = eventSystem;
                _pointerEventData = new PointerEventData(eventSystem);
            }

            _pointerEventData.position = mouse.position.ReadValue();
            s_uiHits.Clear();
            eventSystem.RaycastAll(_pointerEventData, s_uiHits);
            return s_uiHits.Count > 0;
        }

        private void EnsureGhostObject()
        {
            if (_ghostObject != null && _ghostRenderer != null)
            {
                return;
            }

            _ghostObject = new GameObject(GhostObjectName);
            _ghostObject.transform.SetParent(transform, false);
            _ghostObject.transform.localPosition = Vector3.zero;
            _ghostObject.transform.localRotation = Quaternion.identity;
            _ghostObject.transform.localScale = Vector3.one;

            _ghostRenderer = _ghostObject.AddComponent<SpriteRenderer>();
            _ghostRenderer.color = GhostInvalidColor;
            _ghostRenderer.sortingOrder = 1000;

            _ghostSprite = CreateGhostSprite();
            _ghostRenderer.sprite = _ghostSprite;
        }

        private void SetGhostVisible(bool visible)
        {
            if (_ghostRenderer != null)
            {
                _ghostRenderer.enabled = visible;
            }
        }

        private void SetPlacementStatusText(string statusText)
        {
            if (string.Equals(_currentPlacementStatusText, statusText, StringComparison.Ordinal))
            {
                return;
            }

            _currentPlacementStatusText = statusText;
            PlacementStatusChanged?.Invoke(statusText);
        }

        private static string StatusForValidity(PlacementValidity validity)
        {
            switch (validity)
            {
                case PlacementValidity.OK:
                    return StatusPlacingOk;
                case PlacementValidity.OutOfBounds:
                    return StatusOutOfBounds;
                case PlacementValidity.NotWalkableBase:
                    return StatusNotWalkableBase;
                case PlacementValidity.OccupiedByObstacleRect:
                    return StatusOccupied;
                case PlacementValidity.NoCatalog:
                    return StatusNoCatalog;
                case PlacementValidity.WallPrefabNull:
                    return StatusWallPrefabNull;
                case PlacementValidity.NoRequestBuffer:
                    return StatusNoRequestBuffer;
                default:
                    return StatusNoMapData;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void TryLogRuntimePlacementValidationOnce()
        {
            if (_loggedRuntimeValidationOnce)
            {
                return;
            }

            _loggedRuntimeValidationOnce = true;

            bool hasWorld = EnsureQueries();
            if (!hasWorld || _cachedWorld == null || !_cachedWorld.IsCreated)
            {
                Debug.Log("[BuildingPlacementController] Placement runtime check: no ECS world.");
                return;
            }

            EntityManager entityManager = _cachedWorld.EntityManager;
            bool hasCatalog = !_catalogQuery.IsEmptyIgnoreFilter;
            bool hasRequestBuffer = !_requestQueueQuery.IsEmptyIgnoreFilter;
            bool wallPrefabValid = false;

            if (hasCatalog)
            {
                Entity catalogEntity = _catalogQuery.GetSingletonEntity();
                if (entityManager.Exists(catalogEntity))
                {
                    BuildingPrefabCatalog catalog = entityManager.GetComponentData<BuildingPrefabCatalog>(catalogEntity);
                    wallPrefabValid = IsValidWallPrefab(entityManager, catalog.WallPrefab);
                }
            }

            Debug.Log(
                "[BuildingPlacementController] Placement runtime check. " +
                "Catalog: " + (hasCatalog ? "yes" : "no") + ", " +
                "Wall prefab: " + (wallPrefabValid ? "yes" : "no") + ", " +
                "Request buffer: " + (hasRequestBuffer ? "yes" : "no"));
        }
#endif

        private static Sprite CreateGhostSprite()
        {
            return Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f);
        }
    }
}
