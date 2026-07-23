# 21 — Hermes Remote Gateway Auth (Desktop-style UX + automatic session capture)

Companion connects to a **production Hermes** gateway the same way Desktop Settings → Gateway
does: enter a **Gateway URL**, click **Connect / Sign in**, complete normal login, and Companion
**automatically receives the session**. Low-level provider fields (authMode, provider=basic,
cookie paste) are **not** the primary path.

## Desktop sources of truth (re-audit)

| Concern | Desktop file / symbol |
| --- | --- |
| Settings UI: URL + Sign in after probe | `apps/desktop/src/app/settings/gateway-settings.tsx` (`signIn`, probe-driven `authMode`) |
| Probe public status + providers | `apps/desktop/electron/main.ts` `probeRemoteAuthMode` — `GET /api/status` (`auth_required`), `GET /api/auth/providers` |
| OAuth login window + cookie jar poll | `apps/desktop/electron/main.ts` `openOauthLoginWindow` — `BrowserWindow` + `session.fromPartition('persist:hermes-remote-oauth')`, poll `hasOauthSessionCookie` on navigate |
| Cookie-authed REST | `fetchJsonViaOauthSession` — Electron `net` with `useSessionCookies: true` |
| WS URL token vs ticket | `apps/shared/src/websocket-url.ts` `resolveGatewayWsUrl` / `GatewayAuthMode` |
| Connection helpers | `apps/desktop/electron/connection-config.ts` (`normalizeRemoteBaseUrl`, `cookiesHaveSession`) |

Desktop **never** asks the user to paste cookies. HttpOnly `hermes_session_at` / `_rt` are read
from the Electron partition after `/auth/callback` sets them.

## Hermes server auth surface (re-audit)

From `hermes_cli/dashboard_auth/routes.py` + `ws_tickets.py` + plugins:

| Endpoint | Role |
| --- | --- |
| `GET /login` | Server-rendered provider chooser / password form |
| `GET /auth/login?provider=` | Start OAuth (302 to IDP) or bounce password providers to `/login` |
| `GET /auth/callback` | Complete OAuth; **Set-Cookie** session cookies; 302 to `next` or `/` |
| `POST /auth/password-login` | Password providers; JSON + **Set-Cookie** |
| `POST /auth/logout` | Clear cookies |
| `GET /api/auth/providers` | Public provider list (`supports_password`) |
| `GET /api/auth/me` | Auth-required identity |
| `POST /api/auth/ws-ticket` | Cookie-authed single-use WS ticket (30s) |

**Not present** in stock Hermes (no device-code, no native-app redirect, no one-time handoff code).
OAuth `redirect_uri` is always the gateway’s own `/auth/callback` (must end with that path —
see `plugins/dashboard_auth/nous` / `self_hosted` `_validate_redirect_uri`). Therefore a pure
system-browser login leaves cookies only in **that browser’s** jar.

Companion’s automatic capture does **not** require a Hermes server change: it hosts a dedicated
Chromium/Edge profile (partition equivalent) and reads cookies via **Chrome DevTools Protocol**,
the same outcome Desktop gets from `session.cookies.get()`.

Optional forward-compat routes (if Hermes adds them later):  
`GET /auth/native/handoff?redirect_uri=http://127.0.0.1:port/callback` and  
`POST /api/auth/native/redeem` — Companion already implements the client side.

## Two auth modes (backend)

| Mode | REST auth | WS upgrade auth | When |
| --- | --- | --- | --- |
| `token` (default) | `Authorization: Bearer <token>` | `?token=<token>` | Open / loopback gateway (`auth_required: false`) |
| `oauth` | `Cookie: hermes_session_at=…` | `?ticket=<single-use ticket>` | Gated gateway (`auth_required: true`) |

Selection is **automatic** from the probe. Stored as `ProviderConfig.authMode` (`"oauth"` or null).

## Auth flow (mirrors Desktop)

1. **Probe** `GET /api/status` → `auth_required`; when gated, `GET /api/auth/providers`.
2. **Authenticate → session cookie**
   - Password provider: one-shot username/password UI → `POST /auth/password-login` → `Set-Cookie`.
   - Full OAuth (Nous / OIDC): `HermesBrowserOAuthLogin` launches Edge/Chrome with
     `--user-data-dir=<temp> --remote-debugging-port=<n> --app={base}/login`, polls
     `Network.getAllCookies` until `hermes_session_*` for the gateway host appear, then closes
     the window. (Desktop: `openOauthLoginWindow` + partition cookie poll.)
3. **REST** carries the cookie (`Cookie` header).
4. **Mint WS ticket:** `POST /api/auth/ws-ticket` → `{"ticket":…,"ttl_seconds":30}`.
5. **WebSocket** `wss://…/api/ws?ticket=<ticket>`.

## Code map (Companion)

| Concern | File |
| --- | --- |
| Cookie session, password login, ws-ticket, probe | `Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs` |
| **Automatic browser OAuth (CDP cookie capture + optional native handoff)** | `Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs` |
| REST Cookie in OAuth mode; 401 → reauth | `Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs` |
| WS `?ticket=` (token path unchanged) | `Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs` |
| Connect, password login, **browser login**, clear session | `Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs` |
| Desktop-style UI: Gateway URL + Connect / status / Advanced | `Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs` |
| Strings `providers.gateway.*` | `Assets/Resources/Localization/{en,ru}.json` |

## Manual steps for Felix (neon-vps Hermes)

1. Open **Providers**, set global backend to **Hermes (WebSocket)**.
2. Add or edit a Hermes provider; set **Gateway URL** only (e.g. `https://your-host`).
3. Click **Connect / Sign in**.
4. **Password gateway:** username/password appear → enter them → Connect again → status
   **Signed in · connected**.
5. **OAuth (Nous) gateway:** Companion opens an Edge/Chrome app window to `/login` → complete
   normal Hermes/Nous login → window closes when the session is captured → status
   **Signed in · connected**. Chat works without pasting cookies.
6. **Token regression:** Advanced → Bearer token against an open gateway → **Connected (token mode)**.
7. Requirements: Microsoft Edge or Google Chrome installed on the machine (desktop builds).

## Verification

```bash
bash Tools/verify_hermes_auth.sh
```

Static checks include: C# 9 lint, contract wiring, token-mode preservation, Desktop-style UI,
**automatic browser-login path (CDP / HermesBrowserLoginAsync)**, rejected primary labels,
cookie regex + domain filter helpers.
