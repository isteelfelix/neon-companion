# 18_Hermes_Backend_Architecture.md

## Концепция

Hermes — это не провайдер, а **бэкенд**. Полностью другая модель работы приложения:

| | Hermes Backend | OpenAI Backend |
|---|---|---|
| Транспорт | WebSocket JSON-RPC 2.0 | HTTP REST (SSE streaming) |
| Протокол | `wss://domain/api/ws` | `https://provider/v1/chat/completions` |
| Агент | Да (tools, clarify, approval) | Нет |
| Сессии | create/resume/close/interrupt | Stateless |
| Стриминг | WS events (message.delta) | SSE (data: {...}) |
| Управление | REST `/api/*` | REST `/{provider}/*` |
| Фичи | Канбан, крон, навыки, approve | Чистый чат |

Глобальный переключатель бэкенда определяет **режим работы всего приложения**. Это не настройка провайдера — это выбор архитектуры.

---

## Архитектура

### Текущее состояние (до изменений)

```
ProviderConfig { baseUrl, apiKey, backendType: "hermes"|"generic" }
         ↓
ProviderAdapterFactory → IProviderAdapter (HermesAdapter | GenericOpenAiAdapter)
         ↓
OpenAiCompatibleClient (HTTP REST + SSE)
```

Всё работает через HTTP. HermesAdapter — это просто HTTP + заголовки.

### Целевое состояние

```
┌─────────────────────────────────────────────┐
│           GlobalBackendSelector             │
│         BackendMode.Hermes | OpenAI         │
└────────────┬───────────────┬────────────────┘
             │               │
    ┌────────▼────────┐  ┌───▼────────────────┐
    │   Hermes Mode   │  │   OpenAI Mode      │
    │                 │  │                    │
    │ HermesGateway   │  │ OpenAiCompatible   │
    │ (WS JSON-RPC)   │  │ Client (HTTP REST) │
    │                 │  │                    │
    │ HermesRestClient│  │                    │
    │ (REST /api/*)   │  │                    │
    └────────┬────────┘  └───┬────────────────┘
             │               │
             ▼               ▼
    ┌─────────────────────────────────────┐
    │        Unified ChatService          │
    │  (абстрагирует транспорт для UI)    │
    └─────────────────────────────────────┘
```

---

## Компоненты

### 1. GlobalBackendSelector (новый)

**Путь:** `Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs`

MonoBehaviour-синглтон. Хранит текущий `BackendMode`. Определяет какие сервисы/фичи доступны.

```csharp
public enum BackendMode
{
    OpenAI,   // HTTP REST, чистый чат
    Hermes    // WS JSON-RPC, агент, сессии, tools
}

public class GlobalBackendSelector : MonoBehaviour
{
    public BackendMode CurrentMode { get; }
    
    // Событие смены режима
    public event Action<BackendMode> OnModeChanged;
    
    // Фичер-гейт
    public bool IsHermesFeatureAvailable(string feature);
}
```

**При смене режима:**
- Отключает текущий транспорт
- Подключает новый
- Обновляет UI (скрытие/показ элементов)
- Уведомляет все подписанные компоненты

### 2. HermesGateway (новый, WebSocket)

**Путь:** `Assets/Scripts/Runtime/Api/Hermes/HermesGateway.cs`

WebSocket JSON-RPC 2.0 клиент. Основа Hermes-режима.

**Источник:** `/usr/local/lib/hermes-agent/neon-companion-csharp/JsonRpcClient.cs` (референс)

**Протокол:**
```
→ {"jsonrpc":"2.0","id":"r1","method":"session.create","params":{"cols":96}}
← {"jsonrpc":"2.0","id":"r1","result":{"session_id":"..."}}
← {"jsonrpc":"2.0","method":"event","params":{"type":"message.start"}}
← {"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","payload":{"text":"Привет"}}}
← {"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","payload":{"text":"Привет!"}}}
```

**Подключение:** `wss://neon-dev.top/api/ws?token=<session_token>`

**Таймауты:**
- Request timeout: 30s
- Reconnect delay:ponential backoff (1s → 2s → 4s → max 30s)
- Connection timeout: 10s

**Обязательные методы:**
- `Connect(wsUrl)` — подключение
- `Request<T>(method, params)` — RPC запрос
- `On(eventType, handler)` — подписка на событие
- `OnStateChange(handler)` — отслеживание состояния
- `Close()` — закрытие

**Обязательные события (server → client):**
- `gateway.ready` — бэкенд готов
- `session.info` — мета сессии
- `message.start` — начало ответа
- `message.delta` — токен стриминга
- `message.complete` — ответ завершён
- `reasoning.delta` — thinking-токены
- `tool.start` / `tool.progress` / `tool.complete` — tool calls
- `clarify.request` — агент спрашивает
- `approval.request` / `sudo.request` — аппрувалы
- `error` — ошибка

### 3. HermesSessionManager (новый)

**Путь:** `Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs`

Управление жизненным циклом сессий через WS.

**Референс:** `/usr/local/lib/hermes-agent/neon-companion-csharp/SessionManager.cs`

**Методы:**
- `CreateSession(cwd, title)` → `session.create`
- `ResumeSession(sessionId)` → `session.resume`
- `CloseSession()` → `session.close`
- `SubmitPrompt(text)` → `prompt.submit`
- `Interrupt()` → `session.interrupt`
- `RespondToClarify(requestId, answer)` → `clarify.respond`
- `RespondToApproval(requestId, approved)` → `approval.respond`

**Состояние:**
- `ActiveSessionId` — текущая сессия
- `Busy` — идёт генерация
- `AwaitingResponse` — ждём ответ
- `RuntimeInfo` — model, provider, usage

**События (для UI):**
- `OnStreamStarted` / `OnStreamComplete`
- `OnAssistantDelta(text)` / `OnAssistantComplete(text)`
- `OnReasoningDelta(text)`
- `OnToolUpdate(toolPayload)`
- `OnClarifyRequest(request)`
- `OnApprovalRequest(request)`
- `OnError(message)`

### 4. HermesRestClient (новый)

**Путь:** `Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs`

REST management через HTTP. Использует `UnityWebRequest`.

**Эндпоинты (через `wss://neon-dev.top` → nginx → `:8642`):**

Сессии:
- `GET /api/sessions` — список сессий
- `GET /api/sessions/search?q=` — поиск
- `GET /api/sessions/:id/messages` — сообщения сессии
- `DELETE /api/sessions/:id` — удаление
- `PATCH /api/sessions/:id` — переименование

Модели:
- `GET /api/model/info` — текущая модель
- `GET /api/model/options` — доступные модели/провайдеры
- `POST /api/model/set` — смена модели

Конфиг:
- `GET /api/config` / `PUT /api/config` — чтение/запись
- `GET /api/config/schema` — схема полей

Навыки:
- `GET /api/skills` — список
- `PUT /api/skills/toggle` — вкл/выкл

Инструменты:
- `GET /api/tools/toolsets` — список toolset'ов

Крон:
- `GET /api/cron/jobs` — список задач
- `POST /api/cron/jobs` — создание
- `PUT /api/cron/jobs/:id` — обновление
- `DELETE /api/cron/jobs/:id` — удаление

Логи:
- `GET /api/logs` — логи

Аналитика:
- `GET /api/analytics/usage` — использование

### 5. IChatTransport (новый, интерфейс)

**Путь:** `Assets/Scripts/Runtime/Api/IChatTransport.cs`

Абстракция транспорта для ChatService.

```csharp
public interface IChatTransport
{
    bool IsConnected { get; }
    
    Task Connect(string url, string token);
    Task Disconnect();
    
    Task SendMessage(string text);
    Task Interrupt();
    
    // События
    event Action OnStreamStarted;
    event Action<string> OnDelta;
    event Action<string> OnComplete;
    event Action<string> OnReasoningDelta;
    event Action<ToolCallPayload> OnToolUpdate;
    event Action<ClarifyRequestPayload> OnClarifyRequest;
}
```

**Реализации:**
- `HermesWsTransport` — WebSocket JSON-RPC
- `OpenAiHttpTransport` — HTTP REST + SSE (выносим из `OpenAiCompatibleClient`)

### 6. Фичер-гейт

**Реализация:** `GlobalBackendSelector.IsHermesFeatureAvailable(feature)`

**Фичи привязанные к Hermes:**
- `sessions` — создание/восстановление сессий
- `tools` — tool calls, clarify, approval
- `kanban` — канбан-доска
- `cron` — планировщик задач
- `skills` — управление навыками
- `reasoning` — thinking/reasoning стриминг
- `approval` — ручной/approve/auto approve
- `shell` — терминал/команды

**UI интеграция:**
- Навигация: скрыть/показать вкладки
- Кнопки: disable если фича недоступна
- Настройки: показать/скрыть секции

---

## Интеграция с существующим кодом

### Что меняется

1. **`ProviderConfig`** — добавить `BackendMode backendMode` (глобально, не per-provider)
2. **`AppBootstrap`** — инициализация `GlobalBackendSelector` + выбор транспорта
3. **`ChatService`** — работать через `IChatTransport` вместо прямого `OpenAiCompatibleClient`
4. **`MainViewController`** — подписка на `GlobalBackendSelector.OnModeChanged` для UI обновлений

### Что не меняется

1. `OpenAiCompatibleClient` — остаётся для OpenAI режима
2. `GenericOpenAiAdapter` — без изменений
3. `HermesAdapter` — **замещается** `HermesGateway` + `HermesSessionManager` (новый WS-транспорт вместо HTTP-заголовков)
4. Data layer — без изменений
5. Avatar system — без изменений

### Порядок интеграции

```
Phase 1: Транспорт
  ├── IChatTransport (интерфейс)
  ├── HermesGateway (WS JSON-RPC)
  ├── HermesSessionManager (сессии)
  └── HermesRestClient (REST management)

Phase 2: Связывание
  ├── GlobalBackendSelector
  ├── ChatService → IChatTransport
  └── AppBootstrap → выбор транспорта

Phase 3: UI
  ├── Бэкенд-селектор в Providers экране
  ├── Фичер-гейт (навигация, кнопки)
  └── Hermes-специфичный UI (сессии, tools, clarify)

Phase 4: Фичи
  ├── Session history (список сессий)
  ├── Tool calls display
  ├── Clarify flow
  └── Approval flow
```

---

## URL и подключение

**Hermes Backend:**
```
WebSocket: wss://neon-dev.top/api/ws?token=<api_server_key>
REST:      https://neon-dev.top/api/*
```

**Nginx маршрутизация:**
```
wss://neon-dev.top/api/ws  → ws://127.0.0.1:8642/api/ws
https://neon-dev.top/api/* → http://127.0.0.1:8642/api/*
```

**Локальная разработка:**
```
WebSocket: ws://localhost:8642/api/ws
REST:      http://localhost:8642/api/*
```

---

## C# 9 совместимость

Все новые классы должны компилироваться под C# 9 (Unity 6 дефолт):

```
❌ switch expressions
❌ is not null / is not string
❌ tuple deconstruction
❌ target-typed new()
❌ async streams (IAsyncEnumerable)
```

**Корутины vs async/await:**
Проект использует `System.Threading.Tasks.Task`. Не вводить UniTask.

**WebSocket:**
Использовать `System.Net.WebSockets.ClientWebSocket` (доступен в .NET Standard 2.1 / Unity 2022+). Не внешние библиотеки.

**JSON:**
Для runtime моделей — `JsonUtility`. Для десериализации gateway events — `Newtonsoft.Json` (уже используется в проекте).

---

## Питфоли

1. **WebSocketSharp vs ClientWebSocket** — reference использует WebSocketSharp (внешняя библиотека). В neon-companion использовать `System.Net.WebSockets.ClientWebSocket` — он встроен в Unity и не требует дополнительных зависимостей.

2. **Reconnect** — при разрыве WS нужно автоматически переподключаться с exponential backoff. Не переподключаться если пользователь сменил бэкенд на OpenAI.

3. **Thread safety** — WS callback'и приходят не в main thread. Для обновления UI нужно диспатчить в main thread через `await UniTask.SwitchToMainThread()` или `UnitySynchronizationContext`.

4. **Event ordering** — message.start → message.delta*N → message.complete. Если пришёл delta без start — игнорировать или создать виртуальный start.

5. **Session mismatch** — events приходят с `session_id`. Если сессия уже закрыта/сменена — игнорировать stale events.

6. **REST через тот же URL** — HermesRestClient использует `https://neon-dev.top/api/*`. Это тот же домен что и WebSocket, nginx проксирует оба протокола.
