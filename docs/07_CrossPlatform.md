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
- Desktop может получить premium/realtime слой: 3D avatar, blendshapes, visemes, live lipsync
- Генеративные video/avatar pipelines должны быть внешним tooling/backend step, не обязательной частью клиента

### Mobile (Android)
- Ограничения на доступ к файлам
- Использование `Application.persistentDataPath`
- Адаптивный UI
- Основной avatar path для mobile/weak devices — лёгкие 2D sprite-sheet clips/atlases без GPU-зависимости

## Рекомендации
Использовать Unity's Platform Defines (`#if UNITY_ANDROID`, `#if UNITY_STANDALONE`) для разделения логики.