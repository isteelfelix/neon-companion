using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
    public sealed class DefaultPlatformInfoService : IPlatformInfoService
    {
        public Rect SafeArea
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Screen.safeArea;
#else
                return new Rect(0, 0, Screen.width, Screen.height);
#endif
            }
        }

        public bool IsMobile
        {
            get
            {
#if UNITY_ANDROID || UNITY_IOS
                return true;
#else
                return false;
#endif
            }
        }

        public float ScreenDensity => Screen.dpi > 0 ? Screen.dpi : 96f;
    }
}