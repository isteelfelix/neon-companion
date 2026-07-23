# 21 — Hermes Remote Gateway Auth (Desktop-style UX)

Companion connects to a **production Hermes** gateway the same way Desktop Settings → Gateway
does: enter a **Gateway URL**, click **Connect / Sign in**, and complete a normal auth prompt.
Low-level provider fields (authMode, provider=basic, cookie paste) are **not** the primary path.

## Desktop sources of truth (audit)

| Concern | Desktop file / symbol |
| --- | --- |
| Settings UI: URL + Sign in / token box after probe | `apps/desktop/src/app/settings/gateway-settings.tsx` (`signIn`, `authMode` from probe, `oauthConnected`) |
| Probe public status + providers | `apps/desktop/electron/main.ts` `probeRemoteAuthMode` — `GET /api/status` (`auth_required`), `GET /api/auth/providers` |
| OAuth login window (`{base}/login`, cookie jar poll) | `apps/desktop/electron/main.ts` `openOauthLoginWindow` + IPC `hermes:connection-config:oauth-login` |
| WS URL token vs ticket | `apps/shared/src/websocket-url.ts` `resolveGatewayWsUrl` / `GatewayAuthMode` |
| Connection config pure helpers | `apps/desktop/electron/connection-config.ts` (`normalizeRemoteBaseUrl`, cookie liveness) |

Companion cannot host Electron’s OAuth session partition. For password-capable gateways it runs
`POST /auth/password-login` after a one-shot credentials prompt. For pure IDP OAuth it opens the
system browser to `{base}/login` and keeps cookie paste under **Advanced** only.

## Two auth modes (backend)

| Mode | REST auth | WS upgrade auth | When |
| --- | --- | --- | --- |
| `token` (default) | `Authorization: Bearer <token>` | `?token=<token>` | Open / loopback gateway (`auth_required: false`) |
| `oauth` | `Cookie: hermes_session_at=…` | `?ticket=<single-use ticket>` | Gated gateway (`auth_required: true`) |

Selection is **automatic** from the probe. Stored as `ProviderConfig.authMode` (`"oauth"` or null).
Users do not pick “Remote login (cookie)” vs “Token (Bearer)” in the primary UI.

## Auth flow (mirrors Desktop)

1. **Probe** `GET /api/status` → `auth_required` decides oauth vs token; when gated,
   `GET /api/auth/providers` supplies provider name(s) and `supports_password`.
2. **Authenticate → session cookie**
   - Password provider: `POST /auth/password-login` `{provider, username, password}` → `Set-Cookie`.
     Provider name is auto-detected (e.g. `basic`) — never typed by the user.
   - Full OAuth: open `{base}/login` in the system browser; advanced cookie paste if needed.
3. **REST** carries the cookie (`Cookie` header) instead of Bearer.
4. **Mint WS ticket:** `POST /api/auth/ws-ticket` → `{"ticket":…,"ttl_seconds":30}`.
5. **WebSocket** `wss://…/api/ws?ticket=<ticket>`. Fresh ticket per connect; never persisted.

## Code map (Companion)

| Concern | File |
| --- | --- |
| Cookie session, password login, ws-ticket, probe, login URL | `Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs` |
| REST Cookie in OAuth mode; 401 → reauth | `Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs` |
| WS `?ticket=` (token path unchanged) | `Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs` |
| Connect orchestration, login, clear session, reauth state | `Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs` |
| Per-provider auth fields (auto-filled) | `Assets/Scripts/Runtime/Data/Models/ProviderConfig.cs` |
| **Desktop-style UI: Gateway URL + Connect / status / Advanced** | `Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs` |
| Auth fields applied on active provider save | `Assets/Scripts/Runtime/Chat/ChatService.cs` |
| Strings `providers.gateway.*` | `Assets/Resources/Localization/{en,ru}.json` |

### Reauth / error states

`HermesRemoteAuth.State` is `NoSession | Authenticated | ReauthRequired`, via
`GlobalBackendSelector.RemoteAuthState` / `RemoteAuthError` / `HasRemoteSession`.
On 401 the cookie is dropped with reason `no_cookie` | `expired` | `invalid_credentials`.
`ConnectHermes` surfaces `LastConnectionError = "Hermes sign-in required (<reason>)"` and does
**not** spin reconnect until the user signs in again.

Status copy in the editor: **Signed in · connected** / **Signed in** / **Needs sign-in** /
**Connected (token mode)** / **Failed** (last error).

### Secrets

- Session cookie: **in memory only**, never written to disk.
- WS ticket: **never stored**, minted per connect.
- Password: `ISecretStore` key `hermes_pw_<providerId>` for reconnect re-login; cleared on Sign out.
- Password / cookie text fields cleared from the UI after use.

## UI flow (Providers → Hermes)

1. Global backend mode: **Hermes (WebSocket)**.
2. Add/edit a Hermes provider.
3. Enter **Gateway URL** only (e.g. `https://<neon-vps-host>` or path-prefixed).
4. Click **Connect / Sign in**.
5. Companion probes the gateway:
   - **OAuth + password provider:** credentials row appears → enter user/password → Connect again
     → `HermesPasswordLoginAsync` (no duplicate auth client).
   - **OAuth + IDP only:** system browser opens `{url}/login`; Advanced holds cookie fallback.
   - **Token gateway:** Advanced **Bearer token** → Connect.
6. Status shows signed in / connected / needs sign-in / failed.
7. **Sign out** → `ClearHermesRemoteSession`.
8. **Advanced / Token mode** (collapsed): legacy Bearer token + session cookie fallback.

### What is intentionally hidden / removed from the primary path

| Rejected (P8.1) | Replacement |
| --- | --- |
| Auth mode dropdown “Remote login (cookie)” / “Token (Bearer)” | Auto from probe |
| “Login provider (dashboard-auth)” / `basic` field | Auto from `/api/auth/providers` |
| Always-visible username/password/cookie | Credentials only after Connect needs them; cookie under Advanced |
| “Sign in with cookie” as primary button | Advanced “Apply cookie & reconnect” only |

## Manual steps for Felix (neon-vps Hermes)

1. Open **Providers**, set global backend to **Hermes (WebSocket)**.
2. Add or edit a Hermes provider; set **Gateway URL** to the neon-vps base
   (e.g. `https://your-host` — include path prefix if the gateway is reverse-proxied).
3. Click **Connect / Sign in**.
4. If the gateway uses password auth: status becomes **Needs sign-in** and username/password
   appear → enter credentials → **Connect / Sign in** again.
5. If the gateway uses browser OAuth: browser opens `/login` → complete login; if Companion
   cannot capture the session, open **Advanced**, paste the session cookie, **Apply cookie & reconnect**.
6. Expect status **Signed in · connected**; return to chat and send a message (no auth internals required).
7. **Token regression:** open Advanced, enter a Bearer token against an open (`auth_required: false`)
   gateway, Connect → status **Connected (token mode)**; WS uses `?token=`.

## Verification

```bash
bash Tools/verify_hermes_auth.sh
```

Static checks: C# 9 lint, contract wiring, token-mode preservation, Desktop-style UI wiring,
rejected-label absence, cookie regex + probe pure helpers.
