# 07_CrossPlatform.md

## Поддерживаемые платформы (MVP)

- Windows (x64)
- Linux (x64)
- Android

## Планируемые платформы

- iOS
- macOS
- WebGL (ограниченно)

## Особенности сборок

### Desktop
- Полноценный файловый доступ
- Возможность загрузки аватаров из локальных файлов

### Mobile (Android)
- Ограничения на доступ к файлам
- Использование `Application.persistentDataPath`
- Адаптивный UI

## Рекомендации
Использовать Unity's Platform Defines (`#if UNITY_ANDROID`, `#if UNITY_STANDALONE`) для разделения логики.