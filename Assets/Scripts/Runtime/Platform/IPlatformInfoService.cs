using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Информация о текущей платформе и устройстве.
    /// Полезно для адаптивного UI (safe area, плотность экрана, тип устройства).
    /// </summary>
    public interface IPlatformInfoService
    {
        /// <summary>
        /// Safe area экрана (учитывает вырезы, панель навигации и т.д.).
        /// На desktop возвращает полный экран.
        /// </summary>
        Rect SafeArea { get; }

        /// <summary>
        /// Является ли текущая платформа мобильной (Android/iOS).
        /// </summary>
        bool IsMobile { get; }

        /// <summary>
        /// Плотность экрана (для масштабирования UI при необходимости).
        /// </summary>
        float ScreenDensity { get; }
    }
}