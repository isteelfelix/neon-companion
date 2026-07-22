# 20 — Hermes Desktop Contract Parity: Full Review (P6)

**Reviewer:** P6 full-review agent (Claude Opus, `claude-opus-4-8`)
**Date:** 2026-07-22
**Verdict:** ✅ Chain is sound and faithful to the Desktop source-of-truth. No Critical or blocking issues. No junk/revert-needed commits. A small set of Medium/Low hardening items are recommended below (none prevent compile; none applied — review-only per task policy).

> **Runtime limitation:** Unity MCP and a Unity install were **unavailable** on this runner. This is a **textual/contract/static** review. **No Unity compile was performed** — statements about C# correctness are from source inspection against the Unity 6 / C# 9 constraints in `CLAUDE.md`, not from a compiler run.

---

## 1. Reviewed commit range and exact SHAs

Pre-chain base: `fb6da12f31c4237c2732c69a5a7054a1c1995041` (`docs: add creator links`)

Chain (base..HEAD, oldest → newest):

| Phase | SHA (full) | Subject |
|------|------------|---------|
| P1 | `4c235d7cd10d9a73c0a8b6bbb8972cc736230e5a` | docs(hermes): P1 contract freeze — Desktop parity inventory |
| P2 | `352d55584a30dcaf8c1d4fd29432b2220f08a351` | feat(hermes): P2 gateway contract alignment — events, RPC methods, timeouts |
| P4 | `d3dc74066975d182c5b7ffdcd2d4ac99eabfc2c7` | Expand Hermes REST client read surface |
| P3 | `9297b3775b2c21012f795b310df8d5cd0e0fe1a8` | feat(hermes): P3 session-manager routing + agent event plumbing |
| P5 | `24d9f35e4c297b7c02edee64d245bbe6d254af0c` | feat(hermes): P5 wire secret.request to UI + route thinking.delta |

HEAD = `24d9f35e4c297b7c02edee64d245bbe6d254af0c`.

**Commit-state finding (operator concern re: junk commits / bad model):** The five commits are *exactly* the intended P1–P5 chain. Each touches only Hermes-parity files + parity docs; there are **no unrelated, stray, or half-baked commits** on top of the base. Authorship (`OpenAI Codex`, `AoE Runner`, `neon-companion agent`) reflects git author config on the runner, not code quality. **Recommendation: KEEP all five commits.** No reverts required.

Range diffstat (`git diff --stat fb6da12..HEAD`): 9 files, +992 / −46.

---

## 2. Files checked

**Companion (under review):**
- `Assets/Scripts/Runtime/Api/Hermes/HermesGateway.cs`
- `Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs`
- `Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs`
- `Assets/Scripts/Runtime/Api/IChatTransport.cs`
- `Assets/Scripts/Runtime/UI/UITK/Chat/ToolCallApprovalController.cs`
- docs: `19_Hermes_Desktop_Contract_Parity.md`, `18_Hermes_Backend_Architecture.md`, `11_Changelog.md`, `12_Feature_Tracker.md`

**Desktop reference (READ-ONLY source of truth):**
- `/opt/hermes/apps/shared/src/json-rpc-gateway.ts`
- `/opt/hermes/apps/shared/src/websocket-url.ts`
- `/opt/hermes/apps/desktop/src/lib/gateway-events.ts`
- `/opt/hermes/apps/desktop/src/lib/gateway-rpc.ts`
- `/opt/hermes/apps/desktop/src/hermes.ts`
- `/opt/hermes/apps/desktop/src/components/assistant-ui/tool/approval.tsx`, `.../prompt-overlays.tsx`, `.../use-prompt-actions/submit.ts`

---

## 3. Contract parity verification (Companion vs Desktop)

### 3.1 WS event names & routing semantics — ✅ Faithful
- `GatewayEvents` constants cover the full Desktop `GatewayEventName` union and are a superset (companion-specific `terminal.execute`, `session.title`, `file.transfer.*`, subagent subtypes). Superset is safe — Desktop's union is `| (string & {})`.
- `UnscopedStreamEventTypes` (HermesSessionManager) is **exactly** Desktop `UNSCOPED_STREAM_EVENT_TYPES` **minus `browser.progress`** — which Companion has no equivalent event for (documented in-code). Match confirmed field-by-field.
- `EventSessionId(evt)` is a faithful port of `resolveGatewayEventSessionId` (gateway-events.ts):
  - explicit `session_id` always wins; pin released only when *that* session's own turn ends (`message.complete`/`error`);
  - unscoped `subagent.*` → dropped (returns null), never attributed to the focused chat (matches `gatewayEventRequiresSessionId`);
  - `message.start` pins the unscoped stream to the active session; other unscoped stream events resolve to `pin ?? active`; end events release the pin.
  - Ordering of `sid` resolution vs pin mutation matches Desktop.
- Single-dispatch invariant holds: each event type has exactly one dedicated handler that calls `EventSessionId` once; the `"*"` wildcard only forwards `subagent.*` (which does **not** call `EventSessionId`, using `evt.SessionId` directly) — so the pin is never mutated twice for one event.

### 3.2 RPC method names, timeout policy, missing-method — ✅ Match
- Param shapes verified against Desktop:
  - `prompt.submit` → `{ session_id, text }` with `PROMPT_SUBMIT_REQUEST_TIMEOUT_MS` (1,800,000 ms). ✅
  - `approval.respond` → `{ session_id, choice }`, choice ∈ canonical `once`/`session`/`always`/`deny`; Companion sends `once` (approve) / `deny` (reject). ✅
  - `secret.respond` → `{ request_id, value }`. ✅
  - `sudo.respond`, `clarify.respond`, `session.create/resume/close/interrupt/steer`, `slash.exec`, `model.options`, `image.attach_bytes` shapes all consistent.
- **Timeout policy exact match:** `RequestTimeoutMs = 30000` (Desktop `DEFAULT_GATEWAY_REQUEST_TIMEOUT_MS`), `ConnectTimeoutMs = 15000` (`DEFAULT_CONNECT_TIMEOUT_MS`), `PromptSubmitTimeoutMs = 1_800_000` (`PROMPT_SUBMIT_REQUEST_TIMEOUT_MS`). `DefaultTimeoutForMethod` correctly special-cases only `prompt.submit`.
- **Missing-method behavior:** `HermesGateway.IsMissingRpcMethod` matches `method not found` / `-32601` / `unknown method` / `no such method` — **byte-for-byte equivalent** to Desktop `gateway-rpc.ts isMissingRpcMethod` regex.

### 3.3 REST endpoints & response models — ✅ Match (read surface)
All P4 endpoints/paths/params verified against `hermes.ts`:
- `GET /api/sessions?limit&offset=0&min_messages&archived&order` ✅
- `GET /api/sessions/{id}/messages`, `DELETE /api/sessions/{id}` ✅
- `GET /api/status`, `/api/model/info`, `/api/config`, `/api/skills`, `/api/tools/toolsets`, `/api/cron/jobs[?profile]` ✅
- `GET /api/model/options` query flags (`refresh=1`, `include_unconfigured=1`, `explicit_only=1`; `explicitOnly` defaults true → `explicit_only=1`) match `getGlobalModelOptions`. ✅
- Startup timeout budget (`STARTUP=60s`, `SESSION_LIST=60s`, `STATUS=15s`) mirrors Desktop.

### 3.4 Auth / connection URL — ✅ Adequate
`HermesSessionManager.Connect` appends `token=<escaped>` as a query param (query-auth pair), consistent with `buildHermesWebSocketUrl`'s `authParam` behavior. OAuth single-use ticket minting (`resolveGatewayWsUrl`) is **not ported** — acceptable for the token-mode Companion target; note it as a known gap if OAuth remotes become a requirement.

---

## 4. C# / Unity 6 correctness — ✅ Clean (static inspection)

- **No C# 9-forbidden constructs** in the changed files: no switch *expressions* (only a `switch` statement in `HandleGatewayStateChange`), no `is not`/property patterns, no target-typed `new()`, no tuple deconstruction.
- **No `HttpClient`, no `UniTask`.** REST uses `UnityWebRequest` with `int` second timeouts and the standard `while(!op.isDone) await Task.Yield();` main-thread drive. WS uses `System.Net.WebSockets.ClientWebSocket`.
- **`[Serializable]` usage** is correct (`using System;` present).
- **Threading:** gateway captures `SynchronizationContext.Current` at construction and posts event/state callbacks via `InvokeOnContext`, so `HermesSessionManager`'s plain `Dictionary` state and the `_unscopedStreamSessionId` pin are mutated on a single (Unity main) thread. `_pending` is a `ConcurrentDictionary`. No obvious cross-thread Unity API misuse in the reviewed code.
- **Async/await:** all `async` methods have real awaits (no `async`-without-`await`).

---

## 5. Behavioral-risk review

- **prompt.submit ack vs stream completion:** ✅ Correct. `SendMessage` passes the 1.8M-ms timeout so a long turn cannot surface a spurious "request timed out". Busy/awaiting state is driven by `message.start`/`message.complete` events independently of the ack, so the late-resolving ack does not gate the UI.
- **connect timeout cleanup/disposal:** ✅ `Connect` bounds the handshake with a `CancellationTokenSource(ConnectTimeoutMs)`, converts cancellation to `TimeoutException`, disposes the CTS in `finally`, and sets `Error` state on failure. `Close`/`Dispose` cancel the receive loop, dispose the socket, and `RejectAllPending`.
- **missing endpoint detection:** Present (`HermesEndpointMissingException`) but slightly **narrower** than Desktop — see M3.
- **request cancellation / pending cleanup:** ✅ On response, the pending entry is removed and its timeout delay cancelled; on send failure the entry is removed; on close/disconnect all pending are rejected. `HandleGatewayStateChange` clears busy/awaiting maps and fires a connection-level `OnError(null, …)` so ChatService unblocks *all* in-flight generations (prevents the "Выполнение…" hang).
- **Newtonsoft/JToken models:** ✅ `RpcFrame`/`GatewayEvent` use `JToken`/`JObject`; `ExtractText`/`ExtractUsage`/`ParseSessionList` defensively type-check `JTokenType` and swallow per-item parse errors. Compatible with Newtonsoft.

---

## 6. Findings (grouped)

### Critical — none
### High — none

### Medium
- **M1 — `HermesGateway.Request<T>` can throw `NullReferenceException` on a result-less frame.**
  `HermesGateway.cs:331-332`: `var result = await tcs.Task; return result.ToObject<T>();`. When a response frame has an `id` but neither `result` nor `error` (JSON `result` absent → C# `null` JToken), `result.ToObject<T>()` dereferences null. A JSON `"result": null` is safe (it's a `JValue` null → `default(T)`); only an *absent* result field triggers the NRE. Surfaces as a spurious per-call failure (e.g. a false error toast on `prompt.submit`). **Safe fix:** guard `if (result == null) return default(T);` before `ToObject<T>()`. (Not applied — does not break compile.)

- **M2 — `session.list` gateway RPC has no Desktop counterpart.**
  `HermesSessionManager.ListSessions` calls `_gateway.Request<JToken>("session.list", …)`. Desktop lists sessions exclusively over **REST `/api/sessions`** (there is a `session.active_list` gateway method but not `session.list`). If the connected backend does not expose `session.list`, this call fails (and is not routed through `IsMissingRpcMethod`/REST fallback). **Action:** verify the target gateway supports `session.list`; otherwise prefer the REST `ListSessions` path already available in `HermesRestClient`.

- **M3 — Endpoint-missing detection is narrower than Desktop.**
  `HermesRestClient.IsEndpointMissing` requires `status==404` **and** a body containing `"No such API endpoint"` / `"endpoint is likely missing"`. Desktop `isEndpointMissingError` additionally treats a **bare `404`** (empty/other body) as route-missing. A backend returning a plain 404 (no matching body text) will raise a generic `Exception` instead of `HermesEndpointMissingException`, so capability-probe callers can't distinguish "route absent" from a transient failure. **Consider** also treating a bare 404 (for param-less GETs) as endpoint-missing, mirroring Desktop.

### Low
- **L1 — Per-RPC `CancellationTokenSource` is never disposed.** `Request<T>` creates a CTS per call (used to cancel the timeout delay). On completion `HandleMessage` calls `Cts.Cancel()` but never `Dispose()`; the CTS/timer is reclaimed only at GC. Minor; consider disposing in a `finally`.
- **L2 — `Connect` ready-handler leaks on the 5s fallback path.** In `HermesSessionManager.Connect`, if the 5s `Task.Delay` fallback resolves `readyTcs` before `gateway.ready` arrives, `readyHandler` is never `Off`'d and lingers in `_eventHandlers` until a later `gateway.ready` removes it. Harmless (idempotent) but untidy.
- **L3 — `HermesRestClient.Post<T>` / `Patch<T>` are unused private methods.** Dead code for the read-only P4 surface. The C# compiler does not warn on unused private *methods* (unlike fields), so this does **not** break Unity compile; remove or keep for the forthcoming write surface.
- **L4 — `SendMessage` awaits the `prompt.submit` ack.** The ack may resolve late (up to the 30-min ceiling). UI is event-driven so this is benign *as long as* no caller blocks on the returned `Task` before streaming is considered started. Matches Desktop's fire-and-forget intent; flagged only so callers keep relying on `OnStreamStarted`/`OnDelta`, not the ack.

### Positives worth recording
- Timeout constants, missing-method regex, event-name set (minus intentional `browser.progress`), and the session-routing state machine are faithful, well-commented ports of the Desktop source.
- `secret.request` is correctly modeled as a **distinct** text-value capture (masked `TextField`, `secret.respond {request_id, value}`), never routed through approve/deny — matching Desktop `prompt-overlays.tsx`.
- `thinking.delta` / `reasoning.available` correctly share the reasoning path (previously in the unscoped-pin set with no handler → would have been silently dropped; P5 fixes this).

---

## 7. Verification commands & outputs

```
$ git log --format='%h %s' fb6da12..HEAD
24d9f35 feat(hermes): P5 wire secret.request to UI + route thinking.delta
9297b37 feat(hermes): P3 session-manager routing + agent event plumbing
d3dc740 Expand Hermes REST client read surface
352d555 feat(hermes): P2 gateway contract alignment — events, RPC methods, timeouts
4c235d7 docs(hermes): P1 contract freeze — Desktop parity inventory

$ git diff --check fb6da12..HEAD
# → CLEAN (no whitespace/conflict markers)

# C# 9 forbidden-construct scan over the 5 changed .cs files:
#   switch expressions: none (only a switch *statement*)
#   'is not' / property patterns: none
#   target-typed new(): none
#   async-without-await: none
# → PASS

# Repository verification script: NONE present (no *.sh / Makefile / scripts/ at repo root).
# Unity compile: NOT RUN (Unity MCP + Unity install unavailable on this runner).
```

---

## 8. Keep / revert / fix summary

| Commit | Decision | Note |
|--------|----------|------|
| P1 `4c235d7` | **KEEP** | Docs-only contract inventory. |
| P2 `352d555` | **KEEP** | Gateway constants/timeouts/missing-method — verified against Desktop. |
| P4 `d3dc740` | **KEEP** | REST read surface — endpoints/params match. Optional: M3 hardening; L3 dead code. |
| P3 `9297b37` | **KEEP** | Session routing/plumbing — faithful port. Optional: M1/M2. |
| P5 `24d9f35` | **KEEP** | secret.request UI + thinking.delta routing — correct. |

**No commit requires revert.** Recommended follow-ups are the Medium items (M1 trivial NRE guard; M2 verify `session.list`; M3 optional 404 widening) — none blocking, none applied here per review-only policy.
