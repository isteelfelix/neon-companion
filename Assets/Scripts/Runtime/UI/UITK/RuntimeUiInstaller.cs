using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    public sealed class RuntimeUiInstaller : MonoBehaviour
    {
        [SerializeField] private PanelSettings panelSettings;
        [SerializeField] private VisualTreeAsset visualTreeAsset;

        private void Awake()
        {
            if (panelSettings == null || visualTreeAsset == null)
            {
                Debug.LogError("[NeonCompanion] UI installer is missing PanelSettings or VisualTreeAsset.");
                return;
            }

            var document = GetComponent<UIDocument>();
            if (document == null)
                document = gameObject.AddComponent<UIDocument>();

            document.panelSettings = panelSettings;
            document.visualTreeAsset = visualTreeAsset;

            if (GetComponent<MainViewController>() == null)
                gameObject.AddComponent<MainViewController>();
        }
    }
}
