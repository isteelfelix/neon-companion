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

### 1. GlobalBackendSelector

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
    public HermesSessionManager SessionManager { get; }
    public HermesRestClient RestClient { get; }
    public IChatTransport ActiveTransport { get; }

    public event Action<BackendMode> OnModeChanged;
    public bool IsFeatureAvailable(string feature);
}
```

**Жизненный цикл:**
1. `LoadFromSettings()` — загружает saved mode, создаёт транспорт если Hermes
2. `SetMode(Hermes)` → `SetupHermes()` → создаёт Gateway + SessionManager + RestClient
3. `ConnectHermes()` — подключает WS (нужен отдельный вызов!)
4. `OnModeChanged` → `ChatService.SetTransport()` → подписка на события

**Важно:** `SetMode()` создаёт транспорт, но НЕ подключает WS. `ConnectHermes()` — отдельный шаг. `StartNewSessionAsync` и `SwitchToHermesSessionAsync` вызывают его явно.

### 2. HermesGateway

**Путь:** `Assets/Scripts/Runtime/Api/Hermes/HermesGateway.cs`

WebSocket JSON-RPC 2.0 клиент.

**Протокол:**
```
→ {"jsonrpc":"2.0","id":"r1","method":"session.create","params":{"cols":96}}
← {"jsonrpc":"2.0","id":"r1","result":{"session_id":"...","stored_session_id":"..."}}
← {"jsonrpc":"2.0","method":"event","params":{"type":"message.start","session_id":"..."}}
← {"jsonrpc":"2.0","method":"event","params":{"type":"message.delta","session_id":"...","payload":{"text":"Привет"}}}
← {"jsonrpc":"2.0","method":"event","params":{"type":"message.complete","session_id":"...","payload":{"text":"Привет!"}}}
```

**Подключение:** `wss://example.com/api/ws?token=<session_token>`

**Таймауты:**
- Request timeout: 30s
- Reconnect: exponential backoff (1s → 2s → 4s → max 30s)

**RPC Methods:**

| Метод | Назначение | Параметры |
|-------|-----------|-----------|
| `session.create` | Создать сессию | `{cols, cwd?, title?}` |
| `session.resume` | Открыть существующую | `{session_id}` (DB id) |
| `session.close` | Закрыть live-сессию | `{session_id}` (runtime id) |
| `session.list` | Список сессий | `{limit, offset}` |
| `session.interrupt` | Прервать генерацию | `{session_id}` (runtime id) |
| `prompt.submit` | Отправить сообщение | `{session_id, text}` (runtime id) |
| `clarify.respond` | Ответ на clarify | `{session_id, answer}` |
| `approval.respond` | Ответ на approval | `{session_id, choice}` |
| `secret.respond` | Ответ на secret (текстовое значение) | `{request_id, value}` |
| `sudo.respond` | Ответ на sudo (пароль) | `{request_id, password}` |
| `slash.exec` | Выполнить slash-команду | `{session_id, command}` |

**События (server → client):**

| Событие | Payload | Описание |
|---------|---------|----------|
| `session.info` | `SessionRuntimeInfo` | Мета сессии (model, usage, cwd) |
| `message.start` | — | Начало генерации |
| `message.delta` | `{text}` | Токен стриминга |
| `message.complete` | `{text, usage}` | Генерация завершена |
| `reasoning.delta` / `reasoning.available` / `thinking.delta` | `{text}` | Thinking-токены (все → `HandleReasoningDelta`) |
| `message.interim` | `{text}` | Промежуточный текст (тихо; уже стримится через delta) |
| `status.update` | — | Смена фазы (compacting и т.п.); только re-read runtime info, busy не трогает |
| `tool.start` / `tool.progress` / `tool.generating` / `tool.complete` | `ToolEventPayload` | Tool calls (`tool.generating` → та же running-ветка, что `tool.start`) |
| `clarify.request` | `{request_id, question}` | Агент спрашивает |
| `approval.request` / `sudo.request` | `{request_id, question}` | Аппрувалы |
| `secret.request` | `{request_id, env_var, prompt}` | Захват секрета/креда → `OnSecretRequest` (маскированный ввод, `secret.respond`) |
| `session.title` | `{session_id, title}` | Авто-заголовок → `OnSessionTitle` (UI-консьюмер пока не подключён) |
| `subagent.*` | любой | Скоуп-логируется под своей сессией; unscoped **дропается** |
| `background.complete` | — | Фоновая сессия завершена (лог, без панели) |
| `error` | `{message}` | Ошибка |

### 3. HermesSessionManager

**Путь:** `Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs`

Управление жизненным циклом сессий через WS. Реализует `IChatTransport`.

#### Runtime vs Display Session ID

Hermes использует два типа ID:

| Тип | Источник | Назначение | Пример |
|-----|----------|-----------|--------|
| **Runtime ID** | `session.create` → `session_id` | WS RPC: `prompt.submit`, `session.interrupt`, события | `a1b2c3d4` (8 hex) |
| **Display/DB ID** | `session.create` → `stored_session_id` | REST API, UI, история | `k7x9m2n5` (session_key) |

**Правила:**
- `session.create` возвращает оба: `{session_id: runtime, stored_session_id: db}`
- `session.resume` принимает DB id → возвращает новый runtime id
- WS события приходят с runtime id
- Клиент транслирует runtime → display через `_displayByRuntimeSession` mapping

```csharp
// Маппинг ID
private readonly Dictionary<string, string> _runtimeByDisplaySession;
private readonly Dictionary<string, string> _displayByRuntimeSession;

public string RuntimeSessionIdFor(string displayId);
public string DisplaySessionIdFor(string runtimeId);
```

#### Per-session состояние

Было (старое):
```csharp
public string ActiveSessionId { get; }
public bool Busy { get; }
public bool AwaitingResponse { get; }
public SessionRuntimeInfo RuntimeInfo { get; }
```

Стало (multiplexed):
```csharp
// Текущая foreground сессия (для UI и RuntimeInfo)
public string ActiveSessionId { get; }

// Per-session состояние (словари)
private readonly Dictionary<string, bool> _busyBySession;
private readonly Dictionary<string, bool> _awaitingBySession;
private readonly Dictionary<string, SessionRuntimeInfo> _runtimeBySession;

// Query methods
public bool IsSessionBusy(string sessionId);
public SessionRuntimeInfo RuntimeInfoFor(string sessionId);
public void SetForegroundSession(string sessionId);
```

#### Методы

| Метод | Назначение |
|-------|-----------|
| `CreateSession(cwd, title)` | Создать сессию (session.create) |
| `ResumeSession(sessionId)` | Открыть сессию (session.resume) |
| `CloseSession(sessionId)` | Закрыть live-сессию (session.close) |
| `ListSessions(limit)` | Список сессий (session.list) |
| `SendMessage(sessionId, text)` | Отправить сообщение (prompt.submit) |
| `Interrupt(sessionId)` | Прервать генерацию (session.interrupt) |
| `SetForegroundSession(sessionId)` | Установить foreground для UI |
| `IsSessionBusy(sessionId)` | Проверить busy/awaiting |

### 4. IChatTransport

**Путь:** `Assets/Scripts/Runtime/Api/IChatTransport.cs`

Абстракция транспорта для ChatService. **Session-aware** — все события несут `sessionId`.

```csharp
public interface IChatTransport : IDisposable
{
    bool IsConnected { get; }

    Task Connect(string url, string token = null);
    Task Disconnect();

    // Session-aware messaging
    Task SendMessage(string sessionId, string text);
    Task AttachImageBytes(string sessionId, string contentBase64);
    Task Interrupt(string sessionId);

    // Events — all carry sessionId for multiplexed routing
    event Action<string> OnStreamStarted;                    // sessionId
    event Action<string, string> OnDelta;                    // sessionId, text
    event Action<string, string> OnComplete;                 // sessionId, finalText
    event Action<string, string> OnReasoningDelta;           // sessionId, text
    event Action<string, ToolCallUpdate> OnToolUpdate;       // sessionId, update
    event Action<string, ClarifyRequest> OnClarifyRequest;   // sessionId, request
    event Action<string, ApprovalRequest> OnApprovalRequest; // sessionId, request
    event Action<string, string> OnError;                    // sessionId (null = connection-level), message
    event Action<TransportState> OnStateChanged;
}
```

### 5. HermesRestClient

**Путь:** `Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs`

REST management через HTTP. Использует `UnityWebRequest`.

**Эндпоинты (через `wss://example.com` → nginx → `:8642`):**

Статус:
- `GET /api/status` — liveness/readiness backend status

Сессии:
- `GET /api/sessions` — список сессий (`limit`, `offset`, `min_messages`, `archived`, `order`)
- `GET /api/sessions/:id/messages` — сообщения сессии
- `DELETE /api/sessions/:id` — физическое удаление из DB

Модели:
- `GET /api/model/info` — текущая модель
- `GET /api/model/options` — доступные модели/провайдеры

Конфиг:
- `GET /api/config` — чтение текущего конфига

Навыки:
- `GET /api/skills` — список

Инструменты:
- `GET /api/tools/toolsets` — список toolset'ов

Cron:
- `GET /api/cron/jobs` — список cron jobs (`profile` optional)

`HermesRestClient` keeps generic GET/POST/PATCH/DELETE helpers with bearer-token auth, but mutating control-plane methods are intentionally not exposed until UI flows need them. A `404` response whose body says `No such API endpoint` is surfaced as `HermesEndpointMissingException` so callers can degrade instead of retrying a missing backend capability.

### 6. ChatService

**Путь:** `Assets/Scripts/Runtime/Chat/ChatService.cs`

Единый сервис для обоих бэкендов. В Hermes-режиме работает через `IChatTransport` + `HermesSessionManager`.

#### Multiplexed Streams

Для параллельных сессий используется `HermesStream` per display-session-id:

```csharp
private sealed class HermesStream
{
    public string serverSessionId;
    public ChatSession session;
    public ChatViewModel viewModel;
    public ChatMessage streamingMessage;
    public StringBuilder buffer;
    public StringBuilder reasoning;
    public bool active;
    public TaskCompletionSource<bool> complete;
    public Action<string> tokenCb;        // UI callback, set only while foreground
    public Action<string, string, string, string> toolCb;
}

private readonly Dictionary<string, HermesStream> _hermesStreams;
private readonly HashSet<string> _attentionSessions; // pending approval/clarify
```

**Ключевые методы:**
- `GetOrCreateStream(sessionId)` — получить/создать поток для сессии
- `DetachForegroundCallbacks()` — отвязать UI от текущего потока (при переключении)
- `AttachForegroundStreamCallbacks(tokenCb, toolCb)` — повторно привязать UI
- `SessionNeedsAttention(sessionId)` — есть ли pending approval/clarify

#### Жизненный цикл сессии

```
1. Select Provider
   → SetMode(Hermes) → SetupHermes() → _chatTransport установлен
   → НЕ подключён WS!

2. Load Sessions
   → RestClient.ListSessions() → REST API, без WS
   → Список отображается ✅

3. Click Session (Switch)
   → SwitchToHermesSessionAsync()
   → Если WS не подключён → ConnectHermes() → подключение
   → ResumeHermesSessionAsync(dbId) → session.resume → WS RPC
   → Загрузка истории из серверного ответа
   → Переключение foreground

4. Send Message
   → EnsureForegroundSessionIdAsync()
   → SendMessage(sessionId, text) → prompt.submit → WS RPC
   → Streaming events → HermesStream → UI

5. Delete Session
   → CloseSession(runtimeId) → session.close → WS RPC (in-memory cleanup)
   → RestClient.DeleteSession(dbId) → REST DELETE → DB cleanup
```

---

## Сессии: ключевые сценарии

### Создание
```
session.create → {session_id: runtime, stored_session_id: db}
Клиент хранит: providerSessionId = db, providerRuntimeSessionId = runtime
```

### Восстановление
```
session.resume(db_id) → {session_id: new_runtime, messages: [...]}
Клиент загружает историю из ответа, хранит runtime id для RPC
```

### Закрытие
```
session.close(runtime_id) → убирает из in-memory dict
Не удаляет из DB! Только снимает live-привязку.
```

### Удаление
```
session.close(runtime_id) → in-memory cleanup
DELETE /api/sessions/{db_id} → физическое удаление из SQLite
  - DELETE FROM messages WHERE session_id = ?
  - DELETE FROM sessions WHERE id = ?
  - Орфанение дочерних сессий (parent_session_id → NULL)
```

### Параллельный стриминг
```
Сессия A: prompt.submit → message.start → message.delta* → message.complete
Сессия B: prompt.submit → message.start → message.delta* → message.complete
UI показывает foreground; background работает в HermesStream
```

### Переключение mid-stream
```
Foreground: A (стримится)
User кликает B
→ DetachForegroundCallbacks() — A продолжает в фоне
→ ResumeHermesSessionAsync(B) или GetStream(B) — re-attach к B
→ Если B тоже стримится → привязать UI callbacks к B
```

---

## URL и подключение

**Hermes Backend:**
```
WebSocket: wss://example.com/api/ws?token=<api_server_key>
REST:      https://example.com/api/*
```

**Nginx маршрутизация:**
```
wss://example.com/api/ws  → ws://127.0.0.1:8642/api/ws
https://example.com/api/* → http://127.0.0.1:8642/api/*
```

**Локальная разработка:**
```
WebSocket: ws://localhost:8642/api/ws
REST:      http://localhost:8642/api/*
```

---

## Фичер-гейт

**Реализация:** `GlobalBackendSelector.IsFeatureAvailable(feature)`

**Фичи привязанные к Hermes:**
- `sessions` — создание/восстановление сессий
- `tools` — tool calls, clarify, approval
- `kanban` — канбан-доска
- `cron` — планировщик задач
- `skills` — управление навыками
- `reasoning` — thinking/reasoning стриминг
- `approval` — ручной/approve/auto approve
- `shell` — терминал/команды

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

1. **SetMode() не подключает WS** — `SetupHermes()` только создаёт SessionManager. `ConnectHermes()` нужен отдельно. Без него WS RPC молча не работают.

2. **Reconnect** — при разрыве WS автоматически переподключается с exponential backoff. Не переподключается если пользователь сменил бэкенд на OpenAI.

3. **Thread safety** — WS callback'и приходят не в main thread. Для UI обновлений используется Unity main thread dispatch.

4. **Event routing** — события приходят с `runtime session_id`. Клиент транслирует через `DisplaySessionIdFor()` → `ActiveSessionId` comparison. Stale events от закрытых сессий игнорируются.

5. **Session mismatch** — events приходят с `session_id` (runtime). Если сессия уже закрыта/сменена — игнорировать stale events.

6. **REST через тот же URL** — HermesRestClient использует `https://example.com/api/*`. Это тот же домен что и WebSocket, nginx проксирует оба протокола.

7. **Bulk delete** — `db.delete_sessions()` существует в DB-слое, но не экспортируется через REST/WS. Удаление по одной через `DELETE /api/sessions/{id}`.
