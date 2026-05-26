# 01_Architecture.md

## Общая архитектура

Приложение состоит из следующих основных модулей:

- **Core** — базовая логика, управление сессиями, конфигурация провайдеров
- **API Layer** — работа с OpenAI-совместимыми API
- **Avatar System** — управление 2D аватарами, sprite-sheet action sets для low-end/mobile, подготовка к desktop-first 3D realtime аватарам
- **UI Layer** — интерфейс чата и настроек
- **Data Layer** — локальное хранение истории, конфигов и настроек
- **Platform Layer** — специфичный код для Desktop / Mobile / VR

## Технологии
- Unity 2022.3+
- Newtonsoft.Json
- UniTask (для асинхронности)
- Возможно: Zenject или VContainer (DI)

## Диаграмма компонентов (упрощённая)

```
[UI Layer] ↔ [Core] ↔ [API Layer]
                ↕
          [Avatar System]
                ↕
          [Data Layer]
```

## Будущие расширения
- Голосовой ввод/вывод + lipsync
- 3D realtime аватары для desktop
- Генерация motion assets через внешние инструменты/backend pipeline (например LongCat-Video-Avatar-1.5) без обязательной runtime-зависимости клиента
- VR режим
- Локальные модели (через llama.cpp / Ollama)