# 21 — Hermes Remote OAuth / Basic-Auth (P8)

Companion can connect to a **production Hermes** whose dashboard/gateway is gated behind a
Desktop-style session (OAuth or basic-auth), in addition to the legacy token mode.

## Two auth modes

| Mode | REST auth | WS upgrade auth | When |
| --- | --- | --- | --- |
| `token` (default, unchanged) | `Authorization: Bearer <token>` | `?token=<token>` | Loopback / self-hosted with an injected session token |
| `oauth` (new) | `Cookie: hermes_session_at=…` | `?ticket=<single-use ticket>` | Production gated gateway (OAuth/basic-auth) |

Selection is per provider via `ProviderConfig.authMode` (`"oauth"` ⇒ new path; anything else ⇒ token).

## The OAuth/basic-auth flow (mirrors Desktop)

Server contract lives in `hermes_cli/dashboard_auth/` (source of truth):

1. **Authenticate → session cookie.**
   - Basic-auth: `POST /auth/password-login` `{provider, username, password}` → `Set-Cookie:
     hermes_session_at=…` (+ `_rt`, `_provider`). Returns `{"ok":true}`.
   - Full OAuth (browser IDP redirect): sign in in a browser and paste the resulting session
     cookie into Companion — the interactive redirect is intentionally out of scope for the app.
2. **REST calls carry the cookie** (`Cookie` header) instead of a Bearer token.
3. **Mint a WS ticket:** `POST /api/auth/ws-ticket` (cookie-authenticated) → `{"ticket":…,
   "ttl_seconds":30}`. Single-use, 30 s TTL.
4. **Connect the WebSocket** with `wss://…/api/ws?ticket=<ticket>`. A fresh ticket is minted on
   every (re)connect; tickets are **never persisted**.

## Code map (Companion)

| Concern | File |
| --- | --- |
| Cookie session, password login, ws-ticket mint, ticket-URL build | `Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs` |
| REST sends `Cookie` in OAuth mode; 401 → reauth flag | `Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs` |
| WS connects with `?ticket=` (token path unchanged) | `Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs` (`Connect(url, token, ticket)`) |
| Connect orchestration, login entry point, reauth state, clear session | `Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs` |
| Per-provider auth fields | `Assets/Scripts/Runtime/Data/Models/ProviderConfig.cs` |
| Provider editor UI: mode / credentials / login / status / logout | `Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs` |
| Auth field apply on active provider save | `Assets/Scripts/Runtime/Chat/ChatService.cs` (`ApplyProviderConfigAsync`) |
| Localization keys `providers.auth.*` | `Assets/Resources/Localization/{en,ru}.json` |

### Reauth / error states

`HermesRemoteAuth.State` is one of `NoSession | Authenticated | ReauthRequired`, surfaced via
`GlobalBackendSelector.RemoteAuthState` / `RemoteAuthError`. On any 401 the cookie is dropped and a
stable reason is set: `no_cookie`, `expired`, or `invalid_credentials`. `ConnectHermes` translates a
`HermesReauthRequiredException` into `LastConnectionError = "Hermes sign-in required (<reason>)"`
and **does not** spin the reconnect loop (nothing changes until the user signs in again).

The provider editor status row keys off the same state (ok / reauth / last error) and offers
Sign in / Sign in with cookie / Sign out.

### Secrets

- Session cookie: **in memory only**, never written to disk.
- WS ticket: **never stored**, minted per connect.
- Password: kept in the `ISecretStore` under `hermes_pw_<providerId>` (for transparent
  re-login on reconnect), never in `ProviderConfig` JSON, never logged. Cleared on Sign out.
- Password / cookie text fields are cleared from the UI after use.

## UI flow (Providers → Hermes provider)

1. Global backend mode: **Hermes (WebSocket)**.
2. Add/edit a Hermes provider; set **Base URL** (e.g. `https://<neon-vps-host>` or path-prefixed).
3. **Auth mode** → `Remote login (cookie)` (sets `authMode = "oauth"`).
4. Enter **Login provider** (default `basic`), **Username**, **Password**.
5. Press **Sign in** → UI calls `HermesPasswordLoginAsync` (no duplicate auth client).
6. Status row shows session / connected / reauth-required with recoverable retry.
7. **Sign out** → `ClearHermesRemoteSession` (drops cookie, secret-store password, disconnects).
8. Full browser OAuth: paste cookie → **Sign in with cookie** → `SetHermesSessionCookie` + reconnect.
9. Token mode remains the default: API key / Bearer + `?token=` unchanged.

Token-mode providers never show the remote-login sub-fields; leaving cookie mode on save clears
any in-memory remote session before reconnecting with Bearer.

## Connecting Companion to a neon-vps Hermes (basic-auth)

### From UI (preferred)

Follow the UI flow above. A fresh user can configure and connect without editing code or JSON.

### Programmatic (tests / tools)

```csharp
// Password (basic-auth) provider
bool ok = await GlobalBackendSelector.Instance.HermesPasswordLoginAsync(username, password);

// Full OAuth: paste browser session cookie
GlobalBackendSelector.Instance.SetHermesSessionCookie(pastedCookieHeader);
await GlobalBackendSelector.Instance.ConnectHermes();
```

To confirm the gateway is gated: `GET https://<host>/api/status` returns `auth_required: true`.

## Manual scenario checklist

1. **Fresh remote login** — new Hermes provider, oauth mode, base URL + basic credentials → Sign in → status “Session active · connected”; chat works over cookie REST + `?ticket=` WS.
2. **Reconnect / session** — kill WS or restart app with stored secret-store password + authUsername → automatic re-login + ticket mint (as far as P8 plumbing supports).
3. **401 reauth retry** — expire/invalidate session → status shows reauth reason → re-enter password → Sign in recovers.
4. **Token-mode regression** — Hermes provider with Token (Bearer) mode + apiKey still connects via `?token=` / Bearer; remote-auth UI hidden; no cookie/ticket path used.
5. **Sign out** — clears session, disconnects, password field empty; reconnect requires credentials again.

## Verification

No Unity runtime / C# compiler is available in headless CI, so run the deterministic static
check (C# 9 lint + contract wiring + UI wiring + token-mode preservation + cookie-regex behavior):

```bash
bash Tools/verify_hermes_auth.sh   # tracked; .verify/check.sh is the same, but .verify/ is gitignored
```
