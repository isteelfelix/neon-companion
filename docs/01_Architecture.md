# 01_Architecture.md

## Общая архитектура

Приложение состоит из следующих основных модулей:

- **Core** — базовая логика, управление сессиями, конфигурация провайдеров
- **API Layer** — работа с OpenAI-совместимыми API
- **Avatar System** — управление 2D аватарами, sprite-sheet motion packs для low-end/mobile, state mapper (`idle` / `thinking` / `talking` / `listening`) и one-shot reactions (`smile` / `confused`), подготовка к desktop-first 3D realtime аватарам
- **UI Layer** — интерфейс чата и настроек
- **Data Layer** — локальное хранение истории, конфигов, аватаров и motion-pack metadata
- **Platform Layer** — специфичный код для Desktop / Mobile / VR

## Технологии
- Unity 2022.3+
- Newtonsoft.Json
- UniTask (для асинхронности)
- Возможно: Zenject или VContainer (DI)

## Диаграмма компонентов (упрощённая)

```text
[UI Layer] ↔ [Core] ↔ [API Layer]
                ↕
          [Avatar System]
                ↕
          [Data Layer]
```

## Avatar System contract (MVP)
- Runtime читает motion pack (`manifest.json` + sprite sheets)
- State mapper выбирает continuous state: `idle`, `thinking`, `talking`, `listening`
- Reaction policy триггерит `smile` и `confused`
- Формат не решает эмоцию сам по себе; он только описывает доступные клипы

## Будущие расширения
- Голосовой ввод/вывод + lipsync
- 3D realtime аватары для desktop
- Генерация motion assets через внешний asset-pipeline без обязательной runtime-зависимости клиента
- VR режим
- Локальные модели (через llama.cpp / Ollama)
