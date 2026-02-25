using Project.Buildings.Placement;
using UnityEngine;
using UnityEngine.UI;

namespace Project.UI.BuildMenu
{
    [DisallowMultipleComponent]
    public sealed class BuildMenuController : MonoBehaviour
    {
        private const int GridButtonCount = 16;
        private const int ActionButtonIndex = 12; // bottom-left in a 4x4 grid with upper-left start corner

        private const string HeaderObjectName = "BuildMenuHeaderText";
        private const string StatusObjectName = "BuildMenuStatusText";

        private const string RootHeaderText = "Build Menu";
        private const string RootActionLabel = "Deffens Buildings";
        private const string DefenseHeaderText = "Defense Buildings";
        private const string DefenseActionLabel = "Wall";
        private const string WallPlacementStatusText = "Placing: Wall (LMB place, RMB cancel)";

        private enum MenuState : byte
        {
            Root = 0,
            Defense = 1
        }

        private readonly BuildMenuButton[] _buttons = new BuildMenuButton[GridButtonCount];

        private BuildingPlacementController _placementController;
        private Text _headerText;
        private Text _statusText;
        private bool _buttonsBound;
        private MenuState _state;

        private void Awake()
        {
            CacheReferences();
            BindButtons();
        }

        private void OnEnable()
        {
            CacheReferences();
            if (_placementController != null)
            {
                _placementController.WallPlacementModeChanged += OnWallPlacementModeChanged;
                _placementController.PlacementStatusChanged += OnPlacementStatusChanged;
            }

            SetMenuState(MenuState.Root);
            RefreshStatusText();
        }

        private void OnDisable()
        {
            if (_placementController != null)
            {
                _placementController.WallPlacementModeChanged -= OnWallPlacementModeChanged;
                _placementController.PlacementStatusChanged -= OnPlacementStatusChanged;
            }
        }

        private void CacheReferences()
        {
            if (_placementController == null)
            {
                _placementController = GetComponent<BuildingPlacementController>();
            }

            if (_headerText == null || _statusText == null)
            {
                Text[] texts = GetComponentsInChildren<Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    Text text = texts[i];
                    if (text == null)
                    {
                        continue;
                    }

                    if (_headerText == null && text.name == HeaderObjectName)
                    {
                        _headerText = text;
                    }
                    else if (_statusText == null && text.name == StatusObjectName)
                    {
                        _statusText = text;
                    }
                }
            }

            bool hasAllButtons = true;
            for (int i = 0; i < GridButtonCount; i++)
            {
                if (_buttons[i] == null)
                {
                    hasAllButtons = false;
                    break;
                }
            }

            if (!hasAllButtons)
            {
                for (int i = 0; i < GridButtonCount; i++)
                {
                    _buttons[i] = null;
                }

                BuildMenuButton[] foundButtons = GetComponentsInChildren<BuildMenuButton>(true);
                for (int i = 0; i < foundButtons.Length; i++)
                {
                    BuildMenuButton button = foundButtons[i];
                    if (button == null)
                    {
                        continue;
                    }

                    int index = button.Index;
                    if (index < 0 || index >= GridButtonCount)
                    {
                        continue;
                    }

                    if (_buttons[index] == null)
                    {
                        _buttons[index] = button;
                    }
                }
            }
        }

        private void BindButtons()
        {
            if (_buttonsBound)
            {
                return;
            }

            for (int i = 0; i < GridButtonCount; i++)
            {
                BuildMenuButton menuButton = _buttons[i];
                if (menuButton == null || menuButton.Button == null)
                {
                    continue;
                }

                int buttonIndex = i;
                menuButton.Button.onClick.AddListener(() => OnGridButtonClicked(buttonIndex));
            }

            _buttonsBound = true;
        }

        private void OnGridButtonClicked(int buttonIndex)
        {
            if (buttonIndex != ActionButtonIndex)
            {
                return;
            }

            if (_state == MenuState.Root)
            {
                SetMenuState(MenuState.Defense);
                return;
            }

            if (_state == MenuState.Defense && _placementController != null)
            {
                _placementController.BeginWallPlacement();
                RefreshStatusText();
            }
        }

        private void OnWallPlacementModeChanged(bool isPlacingWall)
        {
            RefreshStatusText();
        }

        private void OnPlacementStatusChanged(string statusText)
        {
            RefreshStatusText();
        }

        private void RefreshStatusText()
        {
            if (_statusText == null)
            {
                return;
            }

            bool isPlacingWall = _placementController != null && _placementController.IsPlacingWall;
            if (!isPlacingWall)
            {
                _statusText.text = string.Empty;
                return;
            }

            string placementStatus = _placementController != null ? _placementController.CurrentPlacementStatusText : null;
            if (string.IsNullOrEmpty(placementStatus))
            {
                placementStatus = WallPlacementStatusText;
            }

            _statusText.text = placementStatus;
        }

        private void SetMenuState(MenuState state)
        {
            _state = state;

            if (_headerText != null)
            {
                _headerText.text = _state == MenuState.Root ? RootHeaderText : DefenseHeaderText;
            }

            for (int i = 0; i < GridButtonCount; i++)
            {
                BuildMenuButton button = _buttons[i];
                if (button == null)
                {
                    continue;
                }

                if (i != ActionButtonIndex)
                {
                    button.SetVisual(string.Empty, false);
                    continue;
                }

                if (_state == MenuState.Root)
                {
                    button.SetVisual(RootActionLabel, true);
                }
                else
                {
                    button.SetVisual(DefenseActionLabel, true);
                }
            }
        }
    }
}
