namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Управление "хромом" нативного окна на десктопе: безрамочный режим,
    /// системный ресайз за края, перетаскивание за заданную зону, скругление и тень.
    ///
    /// Реализуется только на Windows-сборке (см. WindowsWindowChromeService).
    /// На остальных платформах — заглушка (см. PlatformServiceFactory).
    ///
    /// Перетаскивание окна инициируется из UITK (client-зона) через BeginDrag(),
    /// чтобы интерактивные элементы в топбаре оставались кликабельными.
    /// </summary>
    public interface IWindowChromeService
    {
        bool IsAvailable { get; }

        /// <summary>true, если окно сейчас развёрнуто (maximized).</summary>
        bool IsMaximized { get; }

        /// <summary>Снять рамку/заголовок, оставить системный ресайз, применить скругление/тень.</summary>
        void ApplyBorderless();

        /// <summary>Вернуть стандартное оформление окна (рамка + заголовок).</summary>
        void RestoreDefault();

        /// <summary>
        /// Начать системное перетаскивание окна. Вызывать на PointerDown по drag-зоне
        /// (например, по фону топбара), пока зажата ЛКМ.
        /// </summary>
        void BeginDrag();

        /// <summary>Развернуть/восстановить окно (поведение двойного клика по заголовку).</summary>
        void ToggleMaximize();

        /// <summary>Свернуть окно в панель задач.</summary>
        void Minimize();
    }
}
