# 07_CrossPlatform.md

**Устаревший документ.** Полная архитектура платформенной поддержки описана в:

→ **[16_Platform_Architecture.md](16_Platform_Architecture.md)**

### Краткая сводка (MVP)

- **Поддерживаемые платформы:** Windows (x64), Android
- **Сцены:** Общие для всех платформ (Boot → Loading → Main)
- **Основной механизм:** Unity 6 Build Profiles + Platform Abstraction Layer через ServiceRegistry
- **UI:** UITK + USS классы + минимальный runtime-код

Подробные правила, структура папок, примеры кода и план реализации — в `16_Platform_Architecture.md`.