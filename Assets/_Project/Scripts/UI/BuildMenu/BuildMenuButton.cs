using UnityEngine;
using UnityEngine.UI;

namespace Project.UI.BuildMenu
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class BuildMenuButton : MonoBehaviour
    {
        [SerializeField] private int _index;
        [SerializeField] private Button _button;
        [SerializeField] private Text _label;

        public int Index => _index;
        public Button Button => _button;
        public Text Label => _label;

        public void SetVisual(string text, bool interactable)
        {
            if (_label != null)
            {
                _label.text = text;
            }

            if (_button != null)
            {
                _button.interactable = interactable;
            }
        }

#if UNITY_EDITOR
        public void EditorConfigure(int index, Button button, Text label)
        {
            _index = index;
            _button = button;
            _label = label;
        }
#endif

        private void Reset()
        {
            CacheLocalReferences();
        }

        private void OnValidate()
        {
            CacheLocalReferences();
        }

        private void CacheLocalReferences()
        {
            if (_button == null)
            {
                _button = GetComponent<Button>();
            }

            if (_label == null)
            {
                _label = GetComponentInChildren<Text>(true);
            }
        }
    }
}
