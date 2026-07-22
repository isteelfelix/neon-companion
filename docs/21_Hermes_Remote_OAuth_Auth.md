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
| Connect orchestration, login entry point, reauth state | `Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs` |
| Per-provider auth fields | `Assets/Scripts/Runtime/Data/Models/ProviderConfig.cs` |

### Reauth / error states

`HermesRemoteAuth.State` is one of `NoSession | Authenticated | ReauthRequired`, surfaced via
`GlobalBackendSelector.RemoteAuthState`. On any 401 the cookie is dropped and a stable reason is
set: `no_cookie`, `expired`, or `invalid_credentials`. `ConnectHermes` translates a
`HermesReauthRequiredException` into `LastConnectionError = "Hermes sign-in required (<reason>)"`
and **does not** spin the reconnect loop (nothing changes until the user signs in again).

### Secrets

- Session cookie: **in memory only**, never written to disk.
- WS ticket: **never stored**, minted per connect.
- Password: kept in the `ISecretStore` under `hermes_pw_<providerId>` (for transparent
  re-login on reconnect), never in `ProviderConfig` JSON, never logged.

## Connecting Companion to a neon-vps Hermes (basic-auth)

1. Add/edit a provider with:
   - `backendType = "hermes"`
   - `baseUrl = "https://<neon-vps-host>"` (include a path prefix if the gateway is proxied, e.g.
     `https://host/hermes`)
   - `authMode = "oauth"`
   - `authProvider = "basic"` (the dashboard-auth provider name; check the server's
     `GET /api/auth/providers`)
2. Sign in once, programmatically:
   ```csharp
   bool ok = await GlobalBackendSelector.Instance.HermesPasswordLoginAsync(username, password);
   ```
   This logs in, caches the password in the secret store, and connects. Reconnects re-login
   automatically.
3. For a full-OAuth gateway (no password provider), obtain the session cookie in a browser and:
   ```csharp
   GlobalBackendSelector.Instance.SetHermesSessionCookie(pastedCookieHeader);
   await GlobalBackendSelector.Instance.ConnectHermes();
   ```

To confirm the gateway is gated: `GET https://<host>/api/status` returns `auth_required: true`.

## Verification

No Unity runtime / C# compiler is available in headless CI, so run the deterministic static
check (C# 9 lint + contract wiring + token-mode preservation + cookie-regex behavior):

```bash
bash Tools/verify_hermes_auth.sh   # tracked; .verify/check.sh is the same, but .verify/ is gitignored
```
