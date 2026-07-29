using NeonCompanion.Runtime.Platform;
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
            if (CompanionProcessMode.IsPlayerProcess)
            {
                var playerDocument = GetComponent<UIDocument>();
                if (playerDocument != null)
                    playerDocument.enabled = false;
                enabled = false;
                return;
            }

            if (panelSettings == null || visualTreeAsset == null)
            {
                Debug.LogError("[NeonCompanion] UI installer is missing PanelSettings or VisualTreeAsset.");
                return;
            }

            var document = GetComponent<UIDocument>();
            if (document == null)
                document = gameObject.AddComponent<UIDocument>();

            // Фундамент адаптива: ширина панели должна отражать физический размер
            // экрана, иначе брейкпоинты не срабатывают. Выставляем в коде, а не
            // полагаемся на сериализованное значение ассета — внешняя правка .asset
            // ненадёжно переимпортируется, а на телефоне это критично.
            //
            // referenceDpi задаёт «во что» переводится физический размер:
            //   mobile  → 160 ⇒ логическая единица = Android dp = iOS point
            //             (телефон ≈ 360–430 «px», планшет ≥ 600 — стандарт sw600dp).
            //   desktop → 96  ⇒ логическая единица = px при 96 dpi (как окно).
            panelSettings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
#if UNITY_ANDROID || UNITY_IOS
            panelSettings.referenceDpi = 160f;
            panelSettings.fallbackDpi = 160f;
#else
            panelSettings.referenceDpi = 96f;
            panelSettings.fallbackDpi = 96f;
#endif

            document.panelSettings = panelSettings;
            document.visualTreeAsset = visualTreeAsset;

            if (GetComponent<MainViewController>() == null)
                gameObject.AddComponent<MainViewController>();

            // Безрамочное окно: перетаскивание/maximize за топбар (Windows; на
            // остальных платформах сервис — заглушка, биндер просто простаивает).
            if (GetComponent<WindowChromeBinder>() == null)
                gameObject.AddComponent<WindowChromeBinder>();
        }
    }
}
