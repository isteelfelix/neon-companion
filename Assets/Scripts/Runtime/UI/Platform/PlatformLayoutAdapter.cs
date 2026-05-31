using UnityEngine;
using UnityEngine.UIElements;
using NeonCompanion.Runtime.Platform;

namespace NeonCompanion.Runtime.UI.Platform
{
    /// <summary>
    /// Применяет платформенные отступы (Safe Area) и CSS-классы к корневому VisualElement.
    /// Используется для адаптации UI под мобильные устройства без засорения контроллеров.
    /// </summary>
    public sealed class PlatformLayoutAdapter
    {
        public void Apply(VisualElement root, IPlatformInfoService platformInfo)
        {
            if (root == null || platformInfo == null)
                return;

            var safeArea = platformInfo.SafeArea;

            // Apply safe area as padding (works for both portrait and landscape)
            float left = Mathf.Max(0, safeArea.xMin);
            float right = Mathf.Max(0, Screen.width - safeArea.xMax);
            float top = Mathf.Max(0, Screen.height - safeArea.yMax);
            float bottom = Mathf.Max(0, safeArea.yMin);

            root.style.paddingLeft = left;
            root.style.paddingRight = right;
            root.style.paddingTop = top;
            root.style.paddingBottom = bottom;

            root.RemoveFromClassList("platform-mobile");
            root.RemoveFromClassList("platform-android");
            root.RemoveFromClassList("platform-ios");

            if (platformInfo.IsMobile || Application.isMobilePlatform)
            {
                root.AddToClassList("platform-mobile");
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                root.AddToClassList("platform-android");
            }
            else if (Application.platform == RuntimePlatform.IPhonePlayer)
            {
                root.AddToClassList("platform-ios");
            }
        }
    }
}
