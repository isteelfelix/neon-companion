# 19 — Hermes Desktop Contract Parity

**Status:** Contract freeze (P1). Docs only — no production C# changed in this pass.
**Goal:** Bring neon-companion's Hermes agent plumbing up to the current **Hermes Desktop** contracts. Desktop is the source of truth for connections, gateway events/RPC, tool/agent wrapping, timeouts, and session identity. This is **not** a UI port of the Electron/React app.

## Sources of truth (read-only reference, `/opt/hermes`)

| Concern | Desktop reference file |
|---|---|
| WS gateway client, event union, request timeouts | `apps/shared/src/json-rpc-gateway.ts` |
| WS URL build + OAuth ticket mint / token auth | `apps/shared/src/websocket-url.ts` |
| REST surface + timeout constants | `apps/desktop/src/hermes.ts` |
| Event routing / unscoped stream pin | `apps/desktop/src/lib/gateway-events.ts` |
| Missing-method / stale-prompt helpers | `apps/desktop/src/lib/gateway-rpc.ts` |
| Multi-profile socket store | `apps/desktop/src/store/gateway.ts` |
| Stream handler (event → transcript) | `apps/desktop/src/app/session/hooks/use-message-stream/gateway-event.ts` |
| Runtime vs stored id mapping | `apps/desktop/src/lib/session-ids.ts` |

Companion edit targets: `Assets/Scripts/Runtime/Api/Hermes/HermesGateway.cs`, `HermesSessionManager.cs`, `HermesRestClient.cs`, `HermesClientCapabilities.cs`, `HermesClientBridge.cs`, `Assets/Scripts/Runtime/Api/IChatTransport.cs`.

Legend: **YES** = present & contract-shaped · **PARTIAL** = present but diverges · **NO** = absent · **N/A** = Desktop-only UI/host concern a remote companion client does not need.
Priority: **P0** = correctness of the agent turn (stream/route/timeout) · **P1** = interactive prompts (approval/clarify/secret/sudo) · **P2** = control-plane / management · **P3** = Desktop-host-only, likely non-goal.

---

## 1. WS Gateway events

Desktop event union: `GatewayEventName` in `json-rpc-gateway.ts:1-23`. Additional runtime event types are consumed in `gateway-event.ts` and pinned/dropped by `gateway-events.ts`. Companion constants live in `HermesGateway.cs:67-87` (`GatewayEvents`), handlers wired in `HermesSessionManager.RegisterEventHandlers` (`HermesSessionManager.cs:585-601`).

| Event | Desktop | Companion | Priority |
|---|---|---|---|
| `gateway.ready` | union + boot | YES (`GatewayReady`, handled) | P0 |
| `session.info` | union + handler | YES (`HandleSessionInfo`) | P0 |
| `message.start` | union + stream pin anchor | YES (`HandleMessageStart`) | P0 |
| `message.delta` | union + handler | YES (`HandleMessageDelta`) | P0 |
| `message.interim` | stream (`gateway-event.ts`) | **NO** | P1 |
| `message.complete` | union + stream end | YES (`HandleMessageComplete`) | P0 |
| `thinking.delta` | stream + unscoped-pin set | **NO** (only `reasoning.delta`) | P1 |
| `reasoning.delta` | union + handler | YES (`HandleReasoningDelta`) | P0 |
| `reasoning.available` | union + unscoped-pin set | **NO** | P1 |
| `status.update` | union + unscoped-pin set | **NO** | P1 |
| `tool.start` | union + handler | YES (`HandleToolStart`) | P0 |
| `tool.progress` | union + handler | YES (`HandleToolProgress`) | P0 |
| `tool.complete` | union + handler (+`inline_diff`) | YES (`HandleToolComplete`) | P0 |
| `tool.generating` | union + unscoped-pin set | **NO** | P1 |
| `clarify.request` | union + handler | YES (`HandleClarifyRequest`) | P1 |
| `approval.request` | union + handler | YES (`HandleApprovalRequest`) | P1 |
| `sudo.request` | union + handler | YES (`HandleSudoRequest`) | P1 |
| `secret.request` | union + handler | **NO** (no handler; no `secret.respond`) | P1 |
| `background.complete` | union | **NO** | P2 |
| `error` | union + handler + stream end | YES (`HandleError`) | P0 |
| `skin.changed` | union | **NO** (avatar/skin — likely non-goal) | P3 |
| `session.title` | stream handler | **NO** | P2 |
| `subagent.spawn_requested` / `subagent.start` / `subagent.*` | stream; **dropped when unscoped** (`gateway-events.ts:55-57`) | **NO** (no subagent drop rule) | P1 |
| `moa.reference` / `moa.aggregating` | stream (MoA presets) | **NO** | P2 |
| `review.summary` | stream | **NO** | P2 |
| `browser.progress` | unscoped-pin set (`gateway-events.ts:20-39`) | **NO** | P2 |
| `agent.terminal.output` | stream | **NO** | P2 |
| `terminal.read.request` | stream + `terminal.read.respond` | PARTIAL — companion uses its own `terminal.execute`/`terminal.respond` pair (divergent method names, see §2) | P1 |
| `terminal.close` | stream | **NO** | P2 |
| `reaction` / `vibe` / `compacting` | stream (UI affect) | **NO** (UI-only, non-goal) | P3 |

**Companion-only events (remote-client extension, NOT in Desktop union):** `client.ping` (→ `client.pong`), `file.transfer.start` / `.chunk` / `.finish` (`HermesGateway.cs:82-86`). These belong to companion's remote client bridge (`HermesClientBridge` + `FileTransfer*`); Desktop is an in-process host and has no equivalent, so keep them but treat as a **companion contract superset**, not a parity gap.

### Highest-value event gaps (P0/P1)
1. **Unscoped stream pin absent.** Desktop `resolveGatewayEventSessionId` (`gateway-events.ts:79-128`) pins every unscoped stream event to the session that last received `message.start`, so a mid-turn chat switch cannot steal live deltas/tool events. Companion `EventSessionId` (`HermesSessionManager.cs:616-621`) resolves `evt.SessionId ?? ActiveSessionId` with **no pin and no `subagent.*` drop** → live output can be misattributed to whichever session is focused.
2. **Missing stream event types:** `message.interim`, `thinking.delta`, `reasoning.available`, `status.update`, `tool.generating`, `secret.request`.

---

## 2. RPC methods (session / agent path)

Desktop calls go through a `requestGateway(method, params, timeoutMs?)` wrapper (session store + `use-prompt-actions` + `use-session-tile-delegate`). Companion methods are the `RpcMethods` constants in `HermesGateway.cs:91-110` plus two inline strings.

| RPC | Desktop | Companion | Priority |
|---|---|---|---|
| `session.create` | YES | YES (`RpcMethods.SessionCreate`) | P0 |
| `session.resume` | YES (`{session_id, cols}`) | YES (does not send `cols`) | P0 |
| `session.close` | YES | YES | P0 |
| `session.interrupt` | YES | YES | P0 |
| `session.steer` | YES | **NO** | P1 |
| `session.active_list` | YES (live sessions across profiles) | PARTIAL — companion uses `session.list` (`HermesSessionManager.ListSessions`); method name diverges | P2 |
| `session.usage` | YES | **NO** (usage derived from `session.info`/`message.complete`) | P2 |
| `session.title` | YES | **NO** | P2 |
| `session.cwd.set` | YES | **NO** | P2 |
| `session.context_breakdown` | YES | **NO** | P2 |
| `session.activate` | YES | **NO** | P2 |
| `prompt.submit` | YES (**`PROMPT_SUBMIT_REQUEST_TIMEOUT_MS` = 1 800 000**) | PARTIAL — sends it, but with the default 30 s timeout (§3) | **P0** |
| `prompt.submit` (rewind) | `truncate_before_user_ordinal` param (`use-prompt-actions/rewind.ts`) | **NO** | P2 |
| `slash.exec` | YES | YES (inline string, `SwitchModelAsync`) | P1 |
| `image.attach` | YES | **NO** | P2 |
| `image.attach_bytes` | YES | YES (`RpcMethods.ImageAttachBytes`) | P1 |
| `image.detach` | YES | **NO** | P2 |
| `file.attach` | YES | PARTIAL — companion has its own `file.transfer.*` protocol instead | P2 |
| `approval.respond` | YES (`{session_id, choice}`) | YES | P1 |
| `clarify.respond` | YES (`{request_id, answer}`) | YES | P1 |
| `secret.respond` | YES | **NO** | P1 |
| `sudo.respond` | YES | **NO** (event handled, no responder) | P1 |
| `terminal.read.respond` | YES | PARTIAL — companion `terminal.respond` (divergent name/shape) | P1 |
| `model.options` | YES | YES (inline string, `GetModelOptionsAsync`) | P2 |
| `commands.catalog` / `complete.path` / `complete.slash` | YES (composer completions) | **NO** | P2 |
| `config.get` / `config.set` | YES | **NO** (companion uses REST/slash) | P2 |
| `reload.env` / `reload.mcp` | YES | **NO** | P3 |
| `process.list` / `process.kill` | YES | **NO** | P3 |
| `llm.oneshot` | YES | **NO** | P3 |
| `handoff.*`, `browser.manage`, `command.dispatch`, `preview.restart`, `pet.*` | YES | **NO** | P3 (Desktop-host-only) |
| `client.register` | (host has no remote register) | YES (`HermesClientBridge.RegisterClientAsync`) | P0 (companion remote-client) |
| `client.pong` | — | YES | P1 |
| `file.transfer.ack/complete/start/chunk/finish` | — | YES (companion extension) | P2 |

**Key RPC gaps:** `prompt.submit` timeout (P0, §3), `session.steer` (P1), `secret.respond` + `sudo.respond` (P1), rewind `truncate_before_user_ordinal` (P2). `session.list` vs Desktop `session.active_list` is a **name divergence** to reconcile — confirm which the backend actually serves for a remote client.

---

## 3. Timeout policy

Desktop constants in `hermes.ts:75-116`; shared client defaults in `json-rpc-gateway.ts:64-68`.

| Constant | Desktop value | Companion | Priority |
|---|---|---|---|
| Shared client default request timeout | `DEFAULT_REQUEST_TIMEOUT_MS = 120_000` | `HermesGateway.RequestTimeoutMs = 30_000` | P1 |
| Desktop gateway client instance request timeout | `DEFAULT_GATEWAY_REQUEST_TIMEOUT_MS = 30_000` | matches companion default | — |
| Connect / open handshake timeout | `DEFAULT_CONNECT_TIMEOUT_MS = 15_000` | **none** — `ClientWebSocket.ConnectAsync(CancellationToken.None)` (`HermesGateway.cs:151`) can hang forever | **P0** |
| Startup read burst | `STARTUP_REQUEST_TIMEOUT_MS = 60_000` | n/a (no boot burst) | P3 |
| Session list | `SESSION_LIST_REQUEST_TIMEOUT_MS = 60_000` | uses 30 s default | P2 |
| **Prompt submit** | **`PROMPT_SUBMIT_REQUEST_TIMEOUT_MS = 1_800_000`** (matches backend `agent.gateway_timeout = 1800s`) | **uses 30 s default** → false "request timed out" on long turns | **P0** |
| Audio speak | `180_000 … 600_000` (length-scaled) | n/a (companion has no audio RPC) | P3 |
| Audio transcribe | `180_000 … 600_000` (length-scaled) | n/a | P3 |
| Hub (skills) | `HUB_REQUEST_TIMEOUT_MS = 45_000` | n/a | P3 |
| `gateway.ready` wait | (implicit via connect state) | fixed `5_000` then proceeds anyway (`HermesSessionManager.cs:317`) | P2 |

**Rationale (from `hermes.ts:78-86`):** `prompt.submit` is effectively fire-and-forget — turn completion is signalled by `message.complete`/`error` stream events, **not** the RPC ack. Bounding the ack at 30 s surfaces a spurious timeout toast while the turn is still running. Companion **must** pass a long timeout for `prompt.submit` (`HermesSessionManager.cs:347-351` currently omits the timeout arg, so `Request<object>` falls back to `RequestTimeoutMs = 30_000`).

**Top timeout fixes:** (1) `prompt.submit` → 1 800 000 ms; (2) add a connect/open-handshake timeout (~15 s) so a dead socket fails to `Error` instead of hanging `Connecting`.

---

## 4. Session identity (runtime id vs stored_session_id)

**Desktop rule** (`session-ids.ts`, `updates.ts:98-118`): the gateway tags every event with the **runtime** session id (key in the gateway's in-memory `_sessions` map). Chat routes are keyed by the **stored** id (`stored_session_id`), assigned when the first turn persists. `runtimeIdByStoredSessionId` maps stored → runtime; `storedSessionIdForNotification` resolves the reverse for notification-click nav. Ids are returned unchanged when no mapping exists (id may already be stored).

**Companion:** `HermesSessionManager` keeps a **bidirectional** map — `_runtimeByDisplaySession` (display/stored → runtime) and `_displayByRuntimeSession` (runtime → display), populated in `RememberSessionIds` from `session.create`/`session.resume` responses (`session_id` = runtime, `stored_session_id` = display). Outbound RPC translates via `RuntimeSessionIdFor`; inbound events translate via `DisplaySessionIdFor` (`EventSessionId`). **Status: YES** — mapping direction matches Desktop; this is companion's strongest parity area (tracker H-07).

Residual gap: companion never persists the stored↔runtime map across reconnects, and `EventSessionId` lacks the unscoped-stream pin (§1) — the *mapping* is correct, the *routing on unscoped events* is not.

---

## 5. Event routing — unscoped stream pin

`gateway-events.ts` (source of truth):
- **`UNSCOPED_STREAM_EVENT_TYPES`** (`:20-39`): `approval.request`, `browser.progress`, `clarify.request`, `error`, `message.complete`, `message.delta`, `message.interim`, `message.start`, `reasoning.available`, `reasoning.delta`, `secret.request`, `status.update`, `sudo.request`, `thinking.delta`, `tool.complete`, `tool.generating`, `tool.progress`, `tool.start`.
- **`UNSCOPED_STREAM_END_EVENT_TYPES`** (`:41`): `error`, `message.complete` — clear the pin.
- **`resolveGatewayEventSessionId`** (`:79-128`): explicit `session_id` always wins (and clears the pin on an end event for that session); `subagent.*` **dropped** when unscoped (`gatewayEventRequiresSessionId`, `:55-57`); `message.start` sets the pin to `activeSessionId`; other unscoped stream events resolve to `unscopedStreamSessionId || activeSessionId`.

**Companion:** `EventSessionId` = `evt.SessionId ?? ActiveSessionId` (`HermesSessionManager.cs:616-621`). **PARTIAL/NO** — no pin state, no end-event clear, no `subagent.*` drop. This is the primary routing correctness gap (P1) and pairs with the missing event types in §1.

---

## 6. Connection / auth (companion as remote client)

`websocket-url.ts`:
- `GatewayAuthMode = 'oauth' | 'token'` (`:1`).
- `resolveGatewayWsUrl` (`:39-94`): for `oauth`, mint a **single-use ticket immediately before opening the socket** via `getGatewayWsUrl(profile)`; on expiry throw `GatewayReauthRequiredError` (`needsOauthLogin`). For `token`, opportunistically mint a fresh URL, else fall back to the stored `wsUrl`.
- `buildHermesWebSocketUrl` (`:135-151`): `ws|wss` from protocol, `authParam` as query pair — usually `["token", value]` or `["ticket", value]`.
- **Multi-profile sockets** (`store/gateway.ts`): one primary socket + one secondary socket per *other* profile that has live work; all feed the same `handleGatewayEvent`. Secondaries are pre-warmed on hover-intent and evicted when idle.

**Companion:** `Connect(url, token)` appends `?token=…`/`&token=…` to the WS URL (`HermesSessionManager.cs:296-303`), single socket, single profile, no OAuth ticket mint, no reauth error surface.

| Concern | Desktop | Companion | Priority |
|---|---|---|---|
| Token query auth | YES | YES | P0 |
| OAuth single-use ticket mint | YES (needs REST mint endpoint) | **NO** | P2 |
| Reauth-required error surface | `GatewayReauthRequiredError` | **NO** | P2 |
| Multi-profile concurrent sockets | YES | **NO** (single socket) | P3 (likely non-goal for companion) |
| `wss` scheme selection | YES (protocol-derived) | relies on caller-supplied URL | P2 |

For a remote companion client, **token auth is the realistic target**; OAuth ticket mint is only needed if the target gateway is OAuth-gated (then a REST mint endpoint must be added). Multi-profile sockets are a Desktop-host convenience — treat as non-goal unless companion needs concurrent cross-profile sessions.

---

## 7. REST control plane (categories only — not to implement in this chain)

Desktop REST paths enumerated from `hermes.ts`. Companion REST is `HermesRestClient` (`/api/sessions*` only). Most control-plane REST is Desktop-host settings UI and **out of scope** for the agent-turn chain.

| REST category | Desktop endpoints (summary) | Companion | Priority |
|---|---|---|---|
| Status / liveness | `/api/status` | **NO** (uses `gateway.ready` + WS state) | P2 |
| Sessions (stored) | Desktop aggregates via gateway; companion `/api/sessions`, `/api/sessions/{id}/messages`, DELETE | PARTIAL (companion-only REST path; confirm backend serves it) | P1 |
| Model | `/api/model/info`, `/api/model/set`, `/api/model/auxiliary`, `/api/model/moa` | PARTIAL (companion switches model via `slash.exec` + `model.options` RPC) | P2 |
| Config | `/api/config`, `/api/config/defaults`, `/api/config/schema` | **NO** | P2 |
| Env | `/api/env`, `/api/env/reveal` | **NO** | P3 |
| Skills | `/api/skills`, `/api/skills/toggle`, `/api/skills/hub/*` | **NO** | P3 |
| MCP | `/api/mcp/servers`, `/api/mcp/catalog`, `/api/mcp/catalog/install` | **NO** | P3 |
| Toolsets / terminal backends | `/api/tools/toolsets`, `/api/tools/terminal/backend(s)`, `/api/tools/computer-use/*` | **NO** | P3 |
| Cron | `/api/cron/jobs` | **NO** | P3 |
| Profiles / providers | `/api/profiles`, `/api/providers/*`, `/api/providers/oauth` | **NO** (note: `/api/providers/oauth` is the OAuth mint path if adopted, §6) | P2 |
| Messaging | `/api/messaging/platforms` | **NO** | P3 |
| Audio | `/api/audio/speak`, `/api/audio/transcribe`, `/api/audio/elevenlabs/voices` | **NO** (companion voice is client-side) | P3 |
| Memory / learning | `/api/memory*`, `/api/learning/*` | **NO** | P3 |
| Ops | `/api/ops/backup`, `/api/ops/doctor`, `/api/ops/security-audit`, `/api/ops/debug-share` | **NO** | P3 |
| Gateway / hermes lifecycle | `/api/gateway/restart`, `/api/hermes/update`, `/api/curator*` | **NO** (tracker C-11 pending) | P3 |

---

## 8. Recommended implementation order

Matches the planned chain **gateway timeouts/events → session manager routing → transport wiring → REST client**:

1. **`HermesGateway` timeouts & connect (P0).** Add a connect/open-handshake timeout (~15 s → `Error` state); raise the shared default toward 120 s; allow per-call timeout override (already supported via `Request<T>(…, timeoutMs)`). Add the missing event **name constants** (`message.interim`, `thinking.delta`, `reasoning.available`, `status.update`, `tool.generating`, `secret.request`, `background.complete`, `session.title`, `subagent.*`).
2. **`HermesSessionManager` routing (P0/P1).** Port `resolveGatewayEventSessionId`: track an `_unscopedStreamSessionId` pin set on `message.start`, cleared on `error`/`message.complete`, drop unscoped `subagent.*`; replace `EventSessionId`'s bare fallback. Add handlers for the new stream events. Pass `PROMPT_SUBMIT_REQUEST_TIMEOUT_MS = 1_800_000` to the `prompt.submit` call. Add `secret.respond` / `sudo.respond` responders and `session.steer`.
3. **Transport wiring (`IChatTransport`) (P1).** Surface `secret.request` (currently only clarify/approval/error events exist on the interface) and interim/thinking deltas so `ChatService` can render them; keep session-id multiplexing.
4. **`HermesRestClient` (P2).** Reconcile `session.list` vs `session.active_list`; confirm `/api/sessions` is served for remote clients or move to the gateway RPC; add `/api/status` liveness if needed. Defer OAuth mint (`/api/providers/oauth`) until an OAuth-gated target is required.

---

## 9. Non-goals (explicit)

- **No UI/UX port** of the Electron/React Desktop app (no screens, panes, composer, statusbar, themes).
- **No Electron / local hermes bootstrap** — companion is a *remote* client; it does not spawn or manage a backend process.
- **No avatar/skin, OpenAI-path, or voice** changes (`skin.changed`, `reaction`, `vibe`, audio REST are out of scope).
- **No Desktop-host-only RPC** (`handoff.*`, `browser.manage`, `pet.*`, `process.*`, `preview.restart`, `command.dispatch`, `reload.*`, `llm.oneshot`).
- **No multi-profile socket fan-out** unless companion needs concurrent cross-profile sessions (Desktop-host convenience).
- **No settings control-plane REST** (config/env/skills/mcp/cron/messaging/ops) beyond what the agent turn requires.

---

## 10. Diff-vs-Desktop summary (what matched / still missing)

**Matched:** JSON-RPC 2.0 framing, request/response id correlation, event dispatch (`method: "event"`), core stream events (`message.*`, `reasoning.delta`, `tool.*`), core session RPC (`create`/`resume`/`close`/`interrupt`/`prompt.submit`), runtime↔stored id mapping (bidirectional), `client.register`/`client.pong` remote handshake, token query auth.

**Still missing (ranked):**
1. `prompt.submit` long timeout (1 800 000 ms) — **P0**.
2. Connect/open-handshake timeout — **P0**.
3. Unscoped stream pin + `subagent.*` drop in event routing — **P1**.
4. Stream event types: `message.interim`, `thinking.delta`, `reasoning.available`, `status.update`, `tool.generating`, `secret.request` — **P1**.
5. `secret.respond` / `sudo.respond` / `session.steer` RPC — **P1**.
6. `session.list` vs `session.active_list` reconciliation, rewind `truncate_before_user_ordinal` — **P2**.
7. OAuth ticket mint + reauth surface — **P2** (only if target is OAuth-gated).

_No changes were made under `/opt/hermes` (read-only reference)._
