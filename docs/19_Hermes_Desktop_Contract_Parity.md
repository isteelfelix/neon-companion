# 19 — Hermes Desktop Contract Parity

**Status:** Contract freeze (P1) + implementation P2–P6. P2 gateway timeouts/events, P3 session-manager routing + agent event plumbing, P5 secret/sudo capture UI + `thinking.delta` routing, P6 session management (title / usage / context breakdown / rewind) are now in code (see the ✅ markers in §1 and §10, and §11 for P6).
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
| `message.interim` | stream (`gateway-event.ts`) | YES (`HandleMessageInterim`, P3) | P1 |
| `message.complete` | union + stream end | YES (`HandleMessageComplete`) | P0 |
| `thinking.delta` | stream + unscoped-pin set | YES (routed to `HandleReasoningDelta`, P5) | P1 |
| `reasoning.delta` | union + handler | YES (`HandleReasoningDelta`) | P0 |
| `reasoning.available` | union + unscoped-pin set | YES (routed to `HandleReasoningDelta`, P3) | P1 |
| `status.update` | union + unscoped-pin set | YES (`HandleStatusUpdate`, P3; re-reads runtime info, no busy toggle) | P1 |
| `tool.start` | union + handler | YES (`HandleToolStart`) | P0 |
| `tool.progress` | union + handler | YES (`HandleToolProgress`) | P0 |
| `tool.complete` | union + handler (+`inline_diff`) | YES (`HandleToolComplete`) | P0 |
| `tool.generating` | union + unscoped-pin set | YES (routed to `HandleToolStart` → `OnToolUpdate`, P3) | P1 |
| `clarify.request` | union + handler | YES (`HandleClarifyRequest`; UI in `ToolCallApprovalController`) | P1 |
| `approval.request` | union + handler | YES (`HandleApprovalRequest`; UI in `ToolCallApprovalController`) | P1 |
| `sudo.request` | union + handler | PARTIAL — surfaced as `OnApprovalRequest` (`type="sudo"`) and answered via `RespondToApproval`; `RespondToSudo(password)` exists but no password-capture UI yet | P1 |
| `secret.request` | union + handler | YES (`HandleSecretRequest` → `OnSecretRequest`; `secret.respond`; masked text prompt UI in `ToolCallApprovalController`, P5) | P1 |
| `background.complete` | union | YES (`HandleBackgroundComplete`, P3; log-only, no background panel) | P2 |
| `error` | union + handler + stream end | YES (`HandleError`) | P0 |
| `skin.changed` | union | **NO** (avatar/skin — likely non-goal) | P3 |
| `session.title` | stream handler | YES (`HandleSessionTitle` → `OnSessionTitle` → `ChatService.OnSessionTitleChanged` → `SessionHistoryController.ApplySessionTitle`; sidebar + topbar rename live, P6) | P2 |
| `subagent.spawn_requested` / `subagent.start` / `subagent.*` | stream; **dropped when unscoped** (`gateway-events.ts:55-57`) | YES (wildcard → `HandleSubagentEvent`, P3; unscoped dropped, scoped logged under owning session) | P1 |
| `moa.reference` / `moa.aggregating` | stream (MoA presets) | **NO** | P2 |
| `review.summary` | stream | **NO** | P2 |
| `browser.progress` | unscoped-pin set (`gateway-events.ts:20-39`) | **NO** | P2 |
| `agent.terminal.output` | stream (keyed by `process_id` only) | YES (`HandleAgentTerminalOutput`) — buffered per owning chat in `AgentTerminalStream`, surfaced as `OnAgentTerminalOutput`; no agent-terminal tabs yet | P2 |
| `terminal.read.request` | stream + `terminal.read.respond` | YES (`HandleTerminalReadRequest` → `terminal.read.respond`); `terminal.execute`/`terminal.respond` remains a companion-only superset, see §2 | P1 |
| `terminal.close` | stream | YES (`HandleTerminalClose`) — drops the process's backlog, fires `OnAgentTerminalClose`; process is not killed | P2 |
| `reaction` / `vibe` / `compacting` | stream (UI affect) | **NO** (UI-only, non-goal) | P3 |

**Companion-only events (remote-client extension, NOT in Desktop union):** `client.ping` (→ `client.pong`), `file.transfer.start` / `.chunk` / `.finish`, and `terminal.execute` (→ `terminal.respond`). These belong to companion's remote client bridge (`HermesClientBridge` + `FileTransfer*` + the `TerminalController` execute bridge); Desktop is an in-process host and has no equivalent, so keep them but treat as a **companion contract superset**, not a parity gap. Upstream `tui_gateway` serves none of them and answers unknown methods with `-32601`, so every superset responder must degrade to a no-op on that code rather than surfacing an error — `RpcException.Code` now carries it (`HermesGateway.IsMissingRpcMethod`).

### Highest-value event gaps (P0/P1)
1. **Unscoped stream pin absent.** Desktop `resolveGatewayEventSessionId` (`gateway-events.ts:79-128`) pins every unscoped stream event to the session that last received `message.start`, so a mid-turn chat switch cannot steal live deltas/tool events. Companion `EventSessionId` (`HermesSessionManager.cs:616-621`) resolves `evt.SessionId ?? ActiveSessionId` with **no pin and no `subagent.*` drop** → live output can be misattributed to whichever session is focused.
2. **Missing stream event types:** `message.interim`, `thinking.delta`, `reasoning.available`, `status.update`, `tool.generating`, `secret.request`.

---

## 2. RPC methods (session / agent path)

Desktop calls go through a `requestGateway(method, params, timeoutMs?)` wrapper (session store + `use-prompt-actions` + `use-session-tile-delegate`). Companion methods are the `RpcMethods` constants in `HermesGateway.cs:91-110` plus two inline strings.

| RPC | Desktop | Companion | Priority |
|---|---|---|---|
| `session.create` | YES | YES (`RpcMethods.SessionCreate`) | P0 |
| `session.resume` | YES (`{session_id, cols, source, profile?}`) | YES — sends `cols`, `source`, and the session's `profile` | P0 |
| `session.close` | YES | YES | P0 |
| `session.interrupt` | YES | YES | P0 |
| `session.steer` | YES | **NO** | P1 |
| `session.active_list` | YES (live sessions across profiles) | YES (`ListActiveSessions`) — live status only, for post-reconnect rehydration; REST `/api/sessions?profile=` stays the history catalog. Missing-method → null, state untouched | P2 |
| `session.usage` | YES | YES (`RequestSessionUsage`, 5 s timeout; merged zero-safe — see §11) | P2 |
| `session.title` | YES (rename RPC) | **NO** — the *event* is consumed (§1); the RPC is a user-driven rename and Companion's sidebar has no rename action to call it from | P2 |
| `session.cwd.set` | YES (`use-cwd-actions.ts`, workspace picker) | **NO / N-A** — Companion has no working-directory surface at all: `LastKnownCwd` is read-only, learned from `session.info` and replayed into `session.create`. Adding the RPC without a picker would be dead code (§11) | P2 |
| `session.context_breakdown` | YES (categories panel) | PARTIAL — `RequestContextBreakdown` is called on session switch and after each `message.complete` for the foreground chat; only `context_max`/`context_used` are applied, because the context bar is the sole consumer. `categories[]` is parsed but intentionally unrendered (§11) | P2 |
| `session.activate` | YES | YES (`ActivateSession`) — rebinds a live session's event transport to the new socket after a reconnect; failure falls back to a full `session.resume` | P2 |
| `prompt.submit` | YES (**`PROMPT_SUBMIT_REQUEST_TIMEOUT_MS` = 1 800 000**) | PARTIAL — sends it, but with the default 30 s timeout (§3) | **P0** |
| `prompt.submit` (rewind) | `truncate_before_user_ordinal` param (`use-prompt-actions/rewind.ts`) | YES (`RewindAndSubmit`; drives regenerate and edit-and-regenerate, §11) | P2 |
| `slash.exec` | YES | YES (inline string, `SwitchModelAsync`) | P1 |
| `image.attach` | YES | **NO** | P2 |
| `image.attach_bytes` | YES | YES (`RpcMethods.ImageAttachBytes`) | P1 |
| `image.detach` | YES | **NO** | P2 |
| `file.attach` | YES | PARTIAL — companion has its own `file.transfer.*` protocol instead | P2 |
| `approval.respond` | YES (`{session_id, choice}`) | YES | P1 |
| `clarify.respond` | YES (`{request_id, answer}`) | YES | P1 |
| `secret.respond` | YES | **NO** | P1 |
| `sudo.respond` | YES | **NO** (event handled, no responder) | P1 |
| `terminal.read.respond` | YES | YES (`RespondToTerminalRead`, `{request_id, text}`) | P1 |
| `terminal.respond` | — | YES (companion extension, answers `terminal.execute`). Upstream has no such method and replies `-32601`; the responder treats that as "not supported", latches it, and stops running the bridge for that connection | P2 |
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

**Key RPC gaps:** `session.list` vs Desktop `session.active_list` is a **name divergence** to reconcile — confirm which the backend actually serves for a remote client.

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

**Id rotation.** `session.resume`/`session.activate` do NOT return `stored_session_id`: the persisted key arrives as `session_key` (fallback `resumed`), and auto-compression makes it the continuation *tip* rather than the id that was asked for. `AdoptResumePayload` keeps the chat's canonical display id and aliases the rotated key onto it (`RememberStoredAlias`), re-pointing only the runtime id used by `prompt.submit`. The `session.info` event carries the live `stored_session_id`, which is the only in-band notice that a chat's key moved (`ReconcileSessionIds`).

**Across reconnects.** The map now survives transport teardown on purpose — `Disconnect()` closes the socket only, never `session.close` — so `RehydrateActiveSessions()` can re-attach the still-live sessions. Bindings the gateway no longer lists are pruned locally (bookkeeping only; the backend session is untouched). A profile switch drops the whole map via `DropLocalSessionState()`, again without any server call.

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

## 7. REST control plane

Desktop REST paths enumerated from `hermes.ts`. Companion REST is `HermesRestClient`; it now exposes a read-only subset of Desktop's management surface for Hermes-native integrations. Most mutating control-plane REST remains Desktop-host settings UI and **out of scope** for the agent-turn chain.

| REST category | Desktop endpoints (summary) | Companion | Priority |
|---|---|---|---|
| Status / liveness | `/api/status` | **YES** (`GetStatus`, short timeout) | P2 |
| Sessions (stored) | Desktop aggregates via gateway; companion `/api/sessions`, `/api/sessions/{id}/messages`, DELETE | PARTIAL (`archived`/`order` query parity added; no Desktop all-profile/sidebar aggregation) | P1 |
| Model | `/api/model/info`, `/api/model/options`, `/api/model/set`, `/api/model/auxiliary`, `/api/model/moa` | PARTIAL (`GET /info` + `GET /options`; companion still switches model via `slash.exec` + `model.options` RPC) | P2 |
| Config | `/api/config`, `/api/config/defaults`, `/api/config/schema` | PARTIAL (`GET /api/config` read-only) | P2 |
| Env | `/api/env`, `/api/env/reveal` | **NO** | P3 |
| Skills | `/api/skills`, `/api/skills/toggle`, `/api/skills/hub/*` | PARTIAL (`GET /api/skills` read-only) | P3 |
| MCP | `/api/mcp/servers`, `/api/mcp/catalog`, `/api/mcp/catalog/install` | **NO** | P3 |
| Toolsets / terminal backends | `/api/tools/toolsets`, `/api/tools/terminal/backend(s)`, `/api/tools/computer-use/*` | PARTIAL (`GET /api/tools/toolsets` read-only) | P3 |
| Cron | `/api/cron/jobs` | PARTIAL (`GET /api/cron/jobs`, optional `profile`) | P3 |
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
3. **Transport wiring (`IChatTransport`) (P1).** ✅ Done (P3/P5): `OnSecretRequest` is on the interface; interim/thinking/reasoning-available deltas are handled in `HermesSessionManager` (interim is silent by design, thinking/reasoning-available route to `OnReasoningDelta`); session-id multiplexing preserved. Secret consumed by `ToolCallApprovalController` (masked text prompt → `secret.respond`).
4. **Remaining REST parity (P2/P3).** Reconcile `session.list` vs `session.active_list`; confirm `/api/sessions` is served for remote clients or move stored-session history fully to gateway RPC. Defer OAuth mint (`/api/providers/oauth`) until an OAuth-gated target is required. Mutating Desktop settings routes stay out of scope until Companion has UI flows for them.

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

**REST matched:** `/api/status`, `/api/model/info`, `/api/model/options`, `/api/config` read-only, `/api/skills`, `/api/tools/toolsets`, `/api/cron/jobs`, and existing stored-session list/messages/delete. Companion mirrors Desktop's 60s startup/list timeout class for slow read endpoints and surfaces missing routes as `HermesEndpointMissingException` when a 404 body says `No such API endpoint`.

**Implemented in code (P2–P5):**
1. `prompt.submit` long timeout (`HermesGateway.PromptSubmitTimeoutMs = 1 800 000`) — **P2**. ✅
2. Connect/open-handshake timeout — **P2**. ✅
3. Unscoped stream pin + `subagent.*` drop in event routing (`EventSessionId`, `HandleSubagentEvent`) — **P3**. ✅
4. Stream event handlers: `message.interim`, `thinking.delta`, `reasoning.available`, `status.update`, `tool.generating`, `secret.request` all registered in `RegisterEventHandlers` — **P3/P5**. ✅
5. `secret.respond` / `sudo.respond` RPC responders (`RespondToSecret` / `RespondToSudo`) — **P2/P3**. ✅
6. **Secret UI (P5):** `OnSecretRequest` surfaced on `IChatTransport`; `ToolCallApprovalController` shows a masked text-input prompt and answers with `secret.respond {request_id, value}` (never routed through the approve/deny responder). Per-session multiplexing preserved (foreground shows inline; background defers via `StorePendingSecret` + attention badge). ✅

7. **Session management (P6):** `session.title` consumed live by the sidebar/topbar; `session.usage` + `session.context_breakdown` applied zero-safely; `prompt.submit` rewind via `truncate_before_user_ordinal`. See §11. ✅

**Still missing / partial (ranked):**
1. `session.list` vs `session.active_list` reconciliation — **P2**.
2. `session.cwd.set`, `session.title` (rename RPC), `moa.*` / `review.summary` / `browser.progress` stream events — **P2**, all blocked on a Companion surface that does not exist rather than on the protocol (§11).
3. OAuth ticket mint + reauth surface — **P2** (only if target is OAuth-gated).
4. Desktop REST mutators and host-only surfaces: config writes/schema/defaults, skill/toolset toggles/config, MCP/catalog, profiles/providers, messaging, audio, memory/learning, ops/update/gateway lifecycle — **P3/out of scope** until Companion grows matching flows.

---

## 11. Session management parity (P6)

### `session.title` — live sidebar rename ✅

The gateway's titler runs *after* the first turn persists and announces the result once, on the
`session.title` stream event; the REST catalog (`/api/sessions`) is only refetched on an explicit
history reload, so before this the sidebar kept showing the preview fallback until the user
navigated away and back.

Chain: `HandleSessionTitle` (payload `session_id` is the STORED/display id — the key the sidebar
renders by) → `IChatTransport.OnSessionTitle` → `ChatService.HandleHermesSessionTitle` →
`ChatService.OnSessionTitleChanged` → `MainViewController.OnSessionTitleChanged` →
`SessionHistoryController.ApplySessionTitle`, which patches the cached row and re-renders (no REST
round-trip); a chat not in the cache yet triggers one refetch. The topbar follows when the renamed
chat is the open one.

`ChatService` keeps the pushed title in `_hermesSessionTitles` as an **overlay only**: the entry is
used while the server row still has no title of its own and dropped the moment the catalog returns
one, so the gateway DB stays the source of truth. The map is cleared on transport swap, because a
mode/profile switch drops the whole session id map with it.

### `session.usage` / `session.context_breakdown` — no zeroing ✅

Both RPCs answer with **partial** objects. `session.usage` for a session whose agent has not been
built yet returns `{calls, input, output, total}` and no context fields at all; the agent-less
branch of `session.context_breakdown` returns an all-zero snapshot (`tui_gateway/server.py`
`session.context_breakdown`, `_session_usage_snapshot` fallback). Deserialized into `UsageStats`
those absent fields land as `0`.

The rule everywhere is therefore **zero means "not reported"**: `MergeUsage` already ignored zeros,
and `ApplyContextBreakdown` now does too (it previously wrote `context_used`/`context_percent`
through on `>= 0`, which is every int, so an agent-less breakdown blanked a gauge that
`session.info`/`message.complete` had filled in). `context_used` also falls back to
`estimated_total`, mirroring the backend's own ordering (measured prompt tokens when the compressor
has them, summed category estimate otherwise).

`message.complete` carries the cumulative counters but not the post-turn prompt size, so
`HandleMessageComplete` now asks for one `session.context_breakdown` when the completed turn belongs
to the **foreground** session — the only session whose gauge is on screen. Fire-and-forget; a
failure leaves the last good numbers.

**Deliberately not consumed:** `categories[]` (Desktop renders them in `context-usage-panel.tsx`;
Companion has no such panel, so the array is parsed and returned but nothing is faked for it),
`context_percent` (the context bar computes its own from used/max), and `credits_lines` from
`session.usage` (no billing surface).

### `prompt.submit` rewind — `truncate_before_user_ordinal` ✅

Companion already had the message action this hangs off: the transcript context menu's **Edit →
Save & Regenerate**, plus the composer's **Regenerate** button. Both funnel through
`ChatController.RegenerateLastAsync`, which in Hermes mode used to call
`ChatViewModel.RegenerateAsync` — the OpenAI HTTP path, which the WebSocket-only backend does not
serve and which would in any case leave the superseded exchange in the agent's context.

`RegenerateOrRewindAsync` now routes Hermes turns to `ChatService.RewindHermesTurnAsync` →
`IChatTransport.RewindAndSubmit` → `prompt.submit {session_id, text, truncate_before_user_ordinal}`.
The ordinal is Desktop's `visibleUserOrdinal`: the zero-based position of the turn among the
transcript's **user messages**, not a transcript row index (`ChatService.UserTurnOrdinal`). A live
turn is interrupted first and an idle one is not, matching `runRewindSubmit` — interrupting an idle
agent can leave a stale interrupt flag that cancels the fresh turn. The key is **omitted** for a
normal submit, since the gateway treats its presence as an explicit rewind.

Known limitations:

- The ordinal is computed from Companion's local transcript. If that has drifted from the server's
  (e.g. server-side auto-compression rewrote the history), the backend rejects the submit and the
  error surfaces through the normal turn-failure path rather than silently truncating the wrong
  turn. Desktop has the same dependency on its local message list.
- The local truncation is optimistic and is **not** rolled back if the submit is rejected — same as
  Desktop's `applyRewindOptimistic`, and same as the pre-existing regenerate path, which already
  dropped the trailing assistant message before calling out. In Hermes the gateway DB is the source
  of truth, so reopening the chat re-syncs the transcript.
- Rewind is Hermes-only. The OpenAI backend has no server-side transcript, so
  `RegenerateOrRewindAsync` falls through to the existing `ChatViewModel.RegenerateAsync` there.

### `session.cwd.set` — not implemented, and why

Desktop's `use-cwd-actions.ts` drives this from a workspace picker in the session header. Companion
has **no working-directory surface of any kind**: `HermesSessionManager.LastKnownCwd` is read-only,
learned from `session.info` and replayed into the next `session.create` so a new chat lands where
the last one did. There is nothing to call the RPC from, and no UI was invented for it — adding the
method alone would be dead code. If a workspace picker is ever added, this is a one-method change
(`session.cwd.set {session_id, cwd}` returns the updated `SessionRuntimeInfo`, and Desktop treats
"unknown method" as "staged locally", not as an error).

_No changes were made under `/opt/hermes` (read-only reference)._
