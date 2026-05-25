# 10_Contribution.md

## Цель
Этот документ описывает базовый workflow для контрибьюторов Neon Companion.

## 1) Подготовка окружения
- Unity: `2022.3 LTS` или новее (`ProjectSettings/ProjectVersion.txt` должен соответствовать).
- Scripting runtime: совместимость с `.NET Standard 2.1`.
- IDE: Visual Studio / Rider с поддержкой Unity.
- После клонирования открой проект через Unity Hub и дождись импорта ассетов.

## 2) Код-стайл
- Namespace для runtime-кода: `NeonCompanion.Runtime.*`.
- Папки кода: `Assets/Scripts/Runtime/<Feature>`.
- Именование:
- `PascalCase` для типов/публичных членов.
- `_camelCase` для приватных полей.
- `camelCase` для локальных переменных/параметров.
- UITK-паттерн:
- UXML/USS лежат в `Assets/UI/...`.
- Runtime-логика UI — в `Assets/Scripts/Runtime/UI/UITK/MainViewController.cs` и связанных view-model/service классах.
- Не смешивай бизнес-логику с прямой манипуляцией визуальными элементами, если это можно вынести в service/model.

## 3) Как добавить новую фичу
### Архитектура (кратко)
- `Core`: инициализация приложения и регистрация сервисов (`AppBootstrap`, `ServiceRegistry`).
- `Data`: модели, репозитории, JSON storage.
- `UI`: binding UITK, обработчики действий, обновление состояния экрана.
- `Plugins`: расширения через `IPlugin` и `PluginContext`.

### Куда класть файлы
- Новый runtime-сервис: `Assets/Scripts/Runtime/<Feature>/`.
- Новый интерфейс: рядом с реализацией (`I...Service.cs`).
- UI-шаблон/стили: `Assets/UI/<Feature>/`.
- Документация: `docs/*.md`.

### Минимальный чеклист
1. Добавь интерфейс и реализацию.
2. Зарегистрируй реализацию в `AppBootstrap` (через `ServiceRegistry`).
3. Подключи использование в UI/других сервисах через DI/реестр сервисов.
4. Обнови документацию и `docs/12_Feature_Tracker.md`.

## 4) Pull Request процесс
1. Сделай fork репозитория.
2. Создай ветку: `feature/<short-name>` или `fix/<short-name>`.
3. Внеси изменения маленькими логическими коммитами.
4. Проверь, что проект открывается и фича работает в Unity Play Mode.
5. Обнови связанные docs при изменении поведения.
6. Открой PR с описанием: что сделано, как проверить, какие ограничения.

## 5) Как добавить новый аватар
### Built-in аватар
1. Добавь изображение в `Assets/UI/Avatars/`.
2. Обнови стили/классы для отображения (например `MainView.Tints.uss` и связанные USS).
3. Добавь id/метаданные в runtime-логику галереи (в `MainViewController`, массив built-in и словарь метаданных).

### Custom аватар
- Загружается пользователем через UI.
- Профиль сохраняется в `avatars.json` через `IAvatarRepository`.
- Файлы копируются в persistent data path (`Application.persistentDataPath/Avatars`).

## 6) Как создать плагин
- Реализуй интерфейс `IPlugin` (`Assets/Scripts/Runtime/Plugins/IPlugin.cs`).
- В `OnInitialize(PluginContext context)`:
- Получай сервисы через `TryGetService<T>()` или `GetRequiredService<T>()`.
- Читай/пиши конфиг через `GetConfig<T>()` / `SetConfig<T>()`.
- Подписывайся на события через `Subscribe<T>()` и публикуй через `Publish<T>()`.
- В `OnShutdown()` освободи ресурсы плагина.

Поля `Id`, `Name`, `Version` должны быть стабильными и уникальными для плагина.
