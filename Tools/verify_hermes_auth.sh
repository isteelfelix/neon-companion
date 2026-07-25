#!/usr/bin/env bash
# Deterministic static verification for the Hermes remote-auth slice (P8 plumbing + Desktop-style UI).
#
# The Companion is a Unity 6 project; no Unity runtime or C# compiler is available in
# CI here, so this script does what CAN be checked deterministically:
#   1. C# 9 compliance lint on the changed files (Unity 6 forbids C# 10+ syntax).
#   2. Auth contract wiring is present (password-login, ws-ticket, ?ticket=, Cookie).
#   3. Legacy token-mode path is preserved unchanged.
#   4. Desktop-style UI wiring: URL + Connect primary path; rejected P8.1 labels gone.
#   5. The cookie-extraction regex actually works on representative Set-Cookie headers
#      (the .NET pattern is re-run with python3, whose regex syntax matches for this pattern).
#   6. Probe helpers (auth_required / providers) are present.
#
# Exit non-zero on any failure.
set -uo pipefail
cd "$(dirname "$0")/.."

FAIL=0
pass() { echo "  PASS: $1"; }
fail() { echo "  FAIL: $1"; FAIL=1; }

FILES=(
  "Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs"
  "Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs"
  "Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs"
  "Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs"
  "Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs"
  "Assets/Scripts/Runtime/Data/Models/ProviderConfig.cs"
  "Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs"
  "Assets/Scripts/Runtime/Chat/ChatService.cs"
)

echo "== [1] Files exist =="
for f in "${FILES[@]}"; do
  if [ -f "$f" ]; then pass "$f"; else fail "missing $f"; fi
done

echo "== [2] C# 9 compliance (Unity 6 constraints) =="
if grep -nE '=\s*[A-Za-z0-9_.\)]+\s+switch\s*\{' "${FILES[@]}" >/dev/null 2>&1; then
  fail "switch expression found (use switch statement)"; else pass "no switch expressions"; fi
if grep -nE '\bis\s+not\s+(null|[A-Z(\{])' "${FILES[@]}" >/dev/null 2>&1; then
  fail "'is not' pattern found (use != / !(x is T))"; else pass "no 'is not' patterns"; fi
if grep -nE '\bis\s+null\b' "${FILES[@]}" >/dev/null 2>&1; then
  fail "'is null' pattern found (use == null)"; else pass "no 'is null' patterns"; fi
if grep -nE '(=|\breturn|\(|,)\s*new\s*\(' "${FILES[@]}" >/dev/null 2>&1; then
  fail "target-typed new() found (use new TypeName())"; else pass "no target-typed new()"; fi
if grep -nE '\[UnityEngine\.Serializable\]' "${FILES[@]}" >/dev/null 2>&1; then
  fail "[UnityEngine.Serializable] found (use [Serializable])"; else pass "no [UnityEngine.Serializable]"; fi

echo "== [3] Auth contract wiring present =="
grep -q '/auth/password-login' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "password-login endpoint" || fail "password-login endpoint missing"
grep -q '/api/auth/ws-ticket' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "ws-ticket endpoint" || fail "ws-ticket endpoint missing"
grep -q 'ticket=' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "?ticket= WS param" || fail "?ticket= WS param missing"
grep -q 'SetRequestHeader("Cookie"' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && grep -q 'SetRequestHeader("Cookie"' Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs \
  && pass "Cookie header on REST + ticket mint" || fail "Cookie header not sent"
grep -q 'MarkReauthRequired' Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs \
  && pass "REST 401 -> reauth flag" || fail "REST 401 reauth not wired"
grep -qE 'no_cookie|invalid_credentials|expired' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "stable reauth reasons (no_cookie/expired/invalid_credentials)" || fail "reauth reasons missing"
if grep -nE 'SetSecret\([^)]*ticket' Assets/Scripts/Runtime/**/*.cs >/dev/null 2>&1; then
  fail "ticket appears to be persisted"; else pass "ticket not persisted"; fi
if grep -nE '(Debug\.Log|NeonLogger)[^\n]*password' Assets/Scripts/Runtime/**/*.cs >/dev/null 2>&1; then
  fail "password may be logged"; else pass "password not logged"; fi

echo "== [4] Legacy token-mode preserved =="
grep -q 'token=' Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs \
  && pass "?token= path kept" || fail "?token= path removed"
grep -q 'Authorization", "Bearer ' Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs \
  && pass "Bearer token path kept" || fail "Bearer token path removed"

echo "== [4b] Desktop-style gateway UI (primary path = URL + Connect) =="
# Positive: required wiring
grep -q 'EnsureGatewayEditorSection' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "Gateway editor section built in C#" || fail "EnsureGatewayEditorSection missing"
grep -q 'OnGatewayConnectClickedAsync' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "Connect / Sign in handler present" || fail "OnGatewayConnectClickedAsync missing"
grep -q 'HermesRemoteAuth.ProbeAsync' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "UI probes gateway via HermesRemoteAuth.ProbeAsync" || fail "ProbeAsync not used by UI"
grep -q '/api/status' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "probe hits /api/status" || fail "/api/status probe missing"
grep -q '/api/auth/providers' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "probe hits /api/auth/providers" || fail "/api/auth/providers probe missing"
grep -q 'BuildLoginUrl' Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs \
  && pass "BuildLoginUrl helper present" || fail "BuildLoginUrl missing"
grep -q 'HermesBrowserLoginAsync' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "UI uses HermesBrowserLoginAsync (Desktop login window)" || fail "UI missing HermesBrowserLoginAsync"
grep -q 'ClearHermesRemoteSession' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "UI calls ClearHermesRemoteSession (Sign out)" || fail "UI missing ClearHermesRemoteSession"
grep -q 'RemoteAuthState' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "UI reads RemoteAuthState" || fail "UI missing RemoteAuthState"
grep -q 'authMode' Assets/Scripts/Runtime/Chat/ChatService.cs \
  && pass "ChatService applies authMode on save" || fail "ChatService does not copy authMode"
# Password must not be written into ProviderConfig from the UI draft builder.
if grep -nE 'draft\.(password|authPassword)\s*=' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "UI appears to assign password into provider draft"; else pass "password not written to provider draft"; fi

# Negative: rejected P8.1 primary labels must not appear in the normal path.
if grep -nF 'Remote login (cookie)' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "rejected label 'Remote login (cookie)' still present"; else pass "no 'Remote login (cookie)' label"; fi
if grep -nF 'Login provider (dashboard-auth)' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "rejected label 'Login provider (dashboard-auth)' still present"; else pass "no login-provider field label"; fi
if grep -nF 'Sign in with cookie' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "rejected primary 'Sign in with cookie' still present"; else pass "no primary 'Sign in with cookie'"; fi
if grep -nF 'AuthModeOAuthValue' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1 \
   || grep -nF 'AuthModeTokenValue' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "authMode dropdown constants still present (user must not pick auth mode)"; else pass "no authMode dropdown constants"; fi
# Primary path must not force the user to type provider=basic as a visible default field.
if grep -nE 'SetValueWithoutNotify\(\s*"basic"\s*\)' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "UI pre-fills provider=basic into a visible field"; else pass "no visible provider=basic prefill"; fi
# Advanced token path must still exist (token-mode regression guard).
grep -q 'gatewayAdvancedToken\|_gatewayAdvancedToken\|Bearer token' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "Advanced Bearer token path present" || fail "Advanced token path missing"

echo "== [4c] Automatic browser-login completion (Desktop parity — FAIL if cookie/creds UI) =="
# Primary OAuth path must wait for automatic session capture (Desktop openOauthLoginWindow).
grep -q 'HermesBrowserLoginAsync' Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs \
  && pass "GlobalBackendSelector.HermesBrowserLoginAsync present" || fail "HermesBrowserLoginAsync missing"
grep -q 'HermesBrowserLoginAsync' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
  && pass "UI Connect calls HermesBrowserLoginAsync" || fail "UI does not call HermesBrowserLoginAsync"
grep -q 'HermesBrowserOAuthLogin.CaptureSessionAsync' Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs \
  && pass "CaptureSessionAsync wired" || fail "CaptureSessionAsync not wired"
grep -q 'Network.getAllCookies' Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs \
  && pass "CDP Network.getAllCookies capture present" || fail "CDP cookie capture missing"
grep -q 'BuildSessionCookieFromCdpGetAllCookiesResponse' Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs \
  && pass "pure CDP→session-cookie handoff helper present" || fail "BuildSessionCookieFromCdpGetAllCookiesResponse missing"
grep -q 'remote-debugging-port' Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs \
  && pass "dedicated Chromium profile + CDP port" || fail "CDP browser launch missing"

# CDP transport: Network.* must use a page/target session, not the browser websocket
# from /json/version (that pattern is unproven / incorrect for Network domain).
CDP_FILE="Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs"
if grep -qE '/json/list|Target\.attachToTarget' "$CDP_FILE"; then
  pass "CDP page target (/json/list) or Target.attachToTarget present"
else
  fail "CDP must use /json/list page target or Target.attachToTarget (not browser-only)"
fi
# Reject the old browser-only discovery helper name.
if grep -q 'TryGetBrowserWebSocketDebuggerUrlAsync' "$CDP_FILE"; then
  fail "still uses TryGetBrowserWebSocketDebuggerUrlAsync (browser websocket)"
else
  pass "no raw browser-websocket discovery helper"
fi
# Prefer proven page-target discovery helper.
if grep -qE 'TryGetPageWebSocketDebuggerUrlAsync|PickPageWebSocketDebuggerUrl|Target\.attachToTarget' "$CDP_FILE"; then
  pass "page-target websocket discovery helper present"
else
  fail "missing page-target discovery (PickPageWebSocketDebuggerUrl / attachToTarget)"
fi
# Fail the unproven pattern: string literal HTTP path /json/version used for debugger discovery.
# Comments may mention the anti-pattern; only executable string literals are rejected.
if grep -nE '"[^"]*/json/version[^"]*"|'\''[^'\'']*/json/version[^'\'']*'\''' "$CDP_FILE" >/dev/null 2>&1; then
  fail "unproven raw /json/version + Network.* browser websocket pattern (string literal)"
else
  pass "no /json/version Network.* transport string"
fi
# Must fetch page list (string literal).
if grep -nE '"[^"]*/json/list[^"]*"' "$CDP_FILE" >/dev/null 2>&1 \
   || grep -q 'Target.attachToTarget' "$CDP_FILE"; then
  pass "/json/list page discovery string or Target.attachToTarget present"
else
  fail "missing /json/list page discovery string literal"
fi
grep -q 'FindChromiumBrowserPath' Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs \
  && pass "browser discovery helper present" || fail "FindChromiumBrowserPath missing"
grep -q 'CookieDomainMatchesHost' Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs \
  && pass "cookie domain filter helper present" || fail "CookieDomainMatchesHost missing"
grep -q '/auth/native/handoff' Assets/Scripts/Runtime/Api/Hermes/HermesBrowserOAuthLogin.cs \
  && pass "optional native handoff path present" || fail "native handoff path missing"

# Desktop: password providers also use the login WINDOW (gateway /login form), not Companion fields.
if grep -n 'HermesPasswordLoginAsync' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "ProvidersController still calls HermesPasswordLoginAsync (in-app credentials path)"; else pass "no in-app password login from UI"; fi
if grep -nE '_gatewayUsername|_gatewayPassword|_gatewayCredentialsWrap' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "username/password credentials UI fields still present"; else pass "no credentials form fields in UI"; fi
if grep -nE 'passwordPath|credentials\.required|edit-gateway-credentials' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "passwordPath / credentials UI branch still present"; else pass "no passwordPath credentials branch"; fi

# Cookie paste is FAIL even as Advanced fallback for the primary product journey.
if grep -nE '_gatewayAdvancedCookie|_gatewayApplyCookieBtn|OnGatewayApplyCookie|Apply cookie' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "cookie paste / Apply cookie UI still present"; else pass "no cookie paste UI"; fi
if grep -q 'SetHermesSessionCookie' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs; then
  fail "UI still calls SetHermesSessionCookie (manual cookie path)"; else pass "UI does not paste cookies"; fi
if grep -nF 'paste the session cookie' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs Assets/Resources/Localization/en.json >/dev/null 2>&1; then
  fail "copy still instructs user to paste session cookie"; else pass "no paste-cookie user instruction"; fi
if grep -n 'Application.OpenURL(loginUrl)' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs >/dev/null 2>&1; then
  fail "Connect OAuth still OpenURL(loginUrl) without CDP capture"; else pass "Connect OAuth is not OpenURL-only"; fi

# ConnectOAuthGatewayAsync body must call HermesBrowserLoginAsync (single automatic path).
if grep -A80 'ConnectOAuthGatewayAsync' Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs \
   | grep -q 'HermesBrowserLoginAsync'; then
  pass "ConnectOAuthGatewayAsync uses HermesBrowserLoginAsync"
else
  fail "ConnectOAuthGatewayAsync missing HermesBrowserLoginAsync"
fi

# Localization keys present in both languages (user-facing gateway strings).
for lang in en ru; do
  if grep -q '"providers.gateway.connect"' "Assets/Resources/Localization/${lang}.json" \
     && grep -q '"providers.gateway.url"' "Assets/Resources/Localization/${lang}.json" \
     && grep -q '"providers.gateway.status.connected"' "Assets/Resources/Localization/${lang}.json" \
     && grep -q '"providers.gateway.sign_out"' "Assets/Resources/Localization/${lang}.json" \
     && grep -q '"providers.gateway.browser.waiting"' "Assets/Resources/Localization/${lang}.json"; then
    pass "providers.gateway.* keys in ${lang}.json"
  else
    fail "providers.gateway.* keys missing in ${lang}.json"
  fi
  # Rejected keys must not reappear as the primary product strings.
  if grep -q 'Remote login (cookie)' "Assets/Resources/Localization/${lang}.json" \
     || grep -q 'Login provider (dashboard-auth)' "Assets/Resources/Localization/${lang}.json"; then
    fail "rejected primary labels present in ${lang}.json"
  else
    pass "no rejected primary labels in ${lang}.json"
  fi
done

echo "== [5] Cookie-extraction + probe pure helpers =="
python3 - <<'PY'
import re, sys
NAMES = [
 "__Host-hermes_session_at","__Secure-hermes_session_at","hermes_session_at",
 "__Host-hermes_session_rt","__Secure-hermes_session_rt","hermes_session_rt",
 "__Host-hermes_session_provider","__Secure-hermes_session_provider","hermes_session_provider",
]
def extract(header):
    out=[]; built=""
    for name in NAMES:
        m=re.search(r"(?:^|[,;\s])"+re.escape(name)+r"=([^;,\s]+)", header)
        if not m: continue
        val=m.group(1)
        base=name.replace("__Host-","").replace("__Secure-","")
        if re.search(r"(?:^|[;\s])(?:__Host-|__Secure-)?"+re.escape(base)+r"=", built):
            continue
        seg=name+"="+val
        built = seg if not built else built+"; "+seg
        out.append(seg)
    return built or None

ok=True
def check(desc, got, want):
    global ok
    if got==want: print(f"  PASS: {desc}")
    else: ok=False; print(f"  FAIL: {desc}  got={got!r} want={want!r}")

# loopback (bare) Set-Cookie, joined by Unity with commas
h1="hermes_session_at=abc.def-123; Path=/; HttpOnly; SameSite=Lax, hermes_session_rt=rt.tok_9; Path=/; HttpOnly, hermes_session_provider=basic; Path=/; HttpOnly"
check("bare cookies", extract(h1), "hermes_session_at=abc.def-123; hermes_session_rt=rt.tok_9; hermes_session_provider=basic")

# HTTPS prefixed __Host- cookies, and bare must not double-match inside prefixed
h2="__Host-hermes_session_at=AAA-bbb; Path=/; Secure; HttpOnly; SameSite=Lax, __Host-hermes_session_rt=CCC; Path=/; Secure; HttpOnly"
check("__Host- cookies (no bare double-match)", extract(h2), "__Host-hermes_session_at=AAA-bbb; __Host-hermes_session_rt=CCC")

# no session cookies present
check("unrelated cookies -> None", extract("csrftoken=xyz; Path=/"), None)
check("empty -> None", extract(""), None)

# auth_required parse (mirrors HermesRemoteAuth.ParseAuthRequired)
def parse_auth_required(status_json):
    import json
    if not status_json:
        return False
    try:
        obj = json.loads(status_json)
        t = obj.get("auth_required")
        if t is True: return True
        if t is False or t is None: return False
        if isinstance(t, str):
            return t.lower() in ("true", "1")
        if isinstance(t, int):
            return t != 0
        return False
    except Exception:
        return False

check("auth_required true", parse_auth_required('{"auth_required":true,"version":"1"}'), True)
check("auth_required false", parse_auth_required('{"auth_required":false}'), False)
check("auth_required missing", parse_auth_required('{"version":"1"}'), False)

# providers parse shape
def parse_providers(providers_json):
    import json
    out=[]
    try:
        obj=json.loads(providers_json)
        for p in obj.get("providers") or []:
            name=p.get("name") or ""
            if not name: continue
            out.append({
                "name": name,
                "display": p.get("display_name") or name,
                "pw": bool(p.get("supports_password")),
            })
    except Exception:
        pass
    return out

got = parse_providers('{"providers":[{"name":"basic","display_name":"Password","supports_password":true}]}')
check("providers parse basic", got, [{"name":"basic","display":"Password","pw":True}])

# Cookie domain filter pure helper (mirrors HermesBrowserOAuthLogin.CookieDomainMatchesHost)
def domain_match(cookie_domain, host):
    if not host:
        return False
    if not cookie_domain:
        return True
    d = cookie_domain.strip().lower()
    h = host.strip().lower()
    if d.startswith("."):
        d = d[1:]
    if d == h:
        return True
    if h.endswith("." + d):
        return True
    return False

check("domain exact", domain_match("gateway.example", "gateway.example"), True)
check("domain parent", domain_match(".example.com", "api.example.com"), True)
check("domain reject", domain_match("other.com", "gateway.example"), False)
check("domain empty cookie -> keep", domain_match("", "gateway.example"), True)

# Machine-verifiable automatic handoff: CDP Network.getAllCookies JSON → session Cookie header
# (mirrors HermesBrowserOAuthLogin.BuildSessionCookieFromCdpGetAllCookiesResponse)
def build_session_cookie_from_cdp(cdp_response_json, gateway_host):
    import json
    if not cdp_response_json or not gateway_host:
        return None
    try:
        obj = json.loads(cdp_response_json)
    except Exception:
        return None
    cookies = (obj.get("result") or {}).get("cookies") or []
    raw_parts = []
    for c in cookies:
        name = c.get("name") or ""
        value = c.get("value")
        domain = c.get("domain")
        if not name or value is None:
            continue
        if not domain_match(domain, gateway_host):
            continue
        raw_parts.append(f"{name}={value}")
    if not raw_parts:
        return None
    return extract("; ".join(raw_parts))

cdp_ok = {
  "id": 2,
  "result": {
    "cookies": [
      {"name": "hermes_session_at", "value": "at.tok-1", "domain": "neon-vps.example"},
      {"name": "hermes_session_rt", "value": "rt.tok-2", "domain": "neon-vps.example"},
      {"name": "hermes_session_provider", "value": "nous", "domain": "neon-vps.example"},
      {"name": "unrelated", "value": "x", "domain": "neon-vps.example"},
      {"name": "hermes_session_at", "value": "other-host", "domain": "evil.example"},
    ]
  }
}
import json as _json
got_cdp = build_session_cookie_from_cdp(_json.dumps(cdp_ok), "neon-vps.example")
check(
    "CDP getAllCookies → session cookie handoff",
    got_cdp,
    "hermes_session_at=at.tok-1; hermes_session_rt=rt.tok-2; hermes_session_provider=nous",
)
# parent-domain rt should match neon-vps.example if domain is .neon-vps.example
cdp_parent = {
  "id": 3,
  "result": {
    "cookies": [
      {"name": "hermes_session_at", "value": "AAA", "domain": ".neon-vps.example"},
      {"name": "hermes_session_rt", "value": "BBB", "domain": ".neon-vps.example"},
    ]
  }
}
check(
    "CDP parent-domain session cookies",
    build_session_cookie_from_cdp(_json.dumps(cdp_parent), "neon-vps.example"),
    "hermes_session_at=AAA; hermes_session_rt=BBB",
)
check(
    "CDP empty cookies → None",
    build_session_cookie_from_cdp('{"id":1,"result":{"cookies":[]}}', "neon-vps.example"),
    None,
)
check(
    "CDP wrong host only → None",
    build_session_cookie_from_cdp(
        _json.dumps({"result":{"cookies":[{"name":"hermes_session_at","value":"z","domain":"other.com"}]}}),
        "neon-vps.example",
    ),
    None,
)

# Handoff completeness: session cookie is enough to mint ws-ticket (contract presence)
# (ws-ticket endpoint already checked in section 3; here we assert the cookie shape
# that MintWsTicketAsync requires — non-empty session cookie header.)
check("handoff cookie non-empty for ticket mint", bool(got_cdp), True)
check("handoff cookie contains access token name", "hermes_session_at=" in (got_cdp or ""), True)

sys.exit(0 if ok else 1)
PY
[ $? -eq 0 ] || FAIL=1

echo "== [6] Session persistence across restarts (secure store only) =="
AUTH="Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs"
SEL="Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs"
REST="Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs"

# Persistence API present on the auth object.
grep -q 'ConfigureSessionPersistence' "$AUTH" \
  && pass "ConfigureSessionPersistence hook" || fail "ConfigureSessionPersistence missing"
grep -q 'TryRestoreSession' "$AUTH" \
  && pass "TryRestoreSession present" || fail "TryRestoreSession missing"
grep -q 'BuildSessionSecretKey' "$AUTH" \
  && pass "endpoint-scoped secret key helper" || fail "BuildSessionSecretKey missing"
grep -q 'ISecretStore' "$AUTH" \
  && pass "persists via ISecretStore abstraction" || fail "ISecretStore not used for session"

# Restore must set Authenticated/HasSession so ConnectHermes needs no fresh sign-in.
if sed -n '/public bool TryRestoreSession/,/^        }/p' "$AUTH" | grep -q 'HermesAuthState.Authenticated'; then
  pass "restored session reports Authenticated"; else fail "TryRestoreSession does not set Authenticated"; fi

# Sign-out AND rejected-session paths must delete the stored copy.
if sed -n '/public void Clear()/,/^        }/p' "$AUTH" | grep -q 'ForgetPersistedSession'; then
  pass "Clear() deletes persisted session"; else fail "Clear() does not delete persisted session"; fi
if sed -n '/public void MarkReauthRequired/,/^        }/p' "$AUTH" | grep -q 'ForgetPersistedSession'; then
  pass "MarkReauthRequired deletes persisted session"; else fail "401/403 path keeps persisted session"; fi

# Refresh semantics: rotating access/refresh cookies are captured and re-persisted.
grep -q 'AdoptRefreshedCookies' "$AUTH" \
  && pass "AdoptRefreshedCookies present" || fail "AdoptRefreshedCookies missing"
grep -q 'MergeCookieHeaders' "$AUTH" \
  && pass "cookie merge helper present" || fail "MergeCookieHeaders missing"
grep -q 'AdoptRefreshedCookies(request.GetResponseHeader("Set-Cookie"))' "$REST" \
  && pass "REST adopts refreshed Set-Cookie" || fail "REST ignores refreshed Set-Cookie"

# Wiring: bound + restored before the first connect; sign-out binds before clearing.
if sed -n '/public void ConfigureHermesEndpoint/,/^        }/p' "$SEL" | grep -q 'SyncRemoteSessionPersistence(true)'; then
  pass "endpoint config restores session before connect"; else fail "ConfigureHermesEndpoint does not restore session"; fi
if sed -n '/public async Task ClearHermesRemoteSession/,/^        }/p' "$SEL" | grep -q 'SyncRemoteSessionPersistence(false)'; then
  pass "sign-out binds persistence before Clear"; else fail "sign-out may orphan the stored session"; fi
if sed -n '/private void SyncRemoteSessionPersistence/,/^        }/p' "$SEL" | grep -q 'BuildSessionSecretKey(provider.id, HermesRestUrl)'; then
  pass "secret key scoped to provider id + endpoint"; else fail "session key not provider/endpoint scoped"; fi
if sed -n '/private void SyncRemoteSessionPersistence/,/^        }/p' "$SEL" | grep -q 'ConfigureSessionPersistence(null, null)'; then
  pass "token-mode / non-Hermes providers get no cookie persistence"; else fail "persistence not detached for token mode"; fi

# The stored session must survive provider-list rewrites (SaveAllAsync prunes secrets).
STORE="Assets/Scripts/Runtime/Data/Secrets/DeviceSecretStore.cs"
grep -q 'SecretIds.AppScopedPrefix + "session_"' "$AUTH" \
  && pass "session key uses the app-scoped secret namespace" || fail "session key outside app-scoped namespace"
if sed -n '/public void DeleteSecretsExcept/,/^        }/p' "$STORE" | grep -q 'SecretIds.IsAppScoped'; then
  pass "provider-list prune keeps app-scoped secrets"; else fail "provider save would prune the stored session"; fi

# No plaintext leaks: no PlayerPrefs, no settings JSON field, no cookie/key in logs or URLs.
# (comment lines are stripped — the docs mention PlayerPrefs as a non-goal)
if grep -hn 'PlayerPrefs' "$AUTH" "$SEL" "$REST" | grep -vE '^[0-9]+:\s*(//|\*)' >/dev/null 2>&1; then
  fail "PlayerPrefs used for Hermes auth state"; else pass "no PlayerPrefs"; fi
if grep -nE '(cookie|Cookie|session_at|session_rt)' Assets/Scripts/Runtime/Data/Models/AppSettings.cs >/dev/null 2>&1; then
  fail "cookie field added to AppSettings JSON"; else pass "no cookie in AppSettings"; fi
if grep -nE '(Debug\.Log[A-Za-z]*|NeonLogger\.Log[A-Za-z]*)\([^)]*(CookieHeader|_cookieHeader|_sessionSecretKey|secretKey)' "$AUTH" "$SEL" "$REST" >/dev/null 2>&1; then
  fail "cookie or secret key may be logged"; else pass "cookie/secret key not logged"; fi
if grep -nE '(\?|&)(cookie|session)=' "$AUTH" "$SEL" "$REST" >/dev/null 2>&1; then
  fail "session material in a URL query"; else pass "no session material in URLs"; fi
# Tickets stay single-use: never handed to the secret store.
if grep -nE 'SetSecret\([^)]*[Tt]icket|ticket[^)]*SetSecret' "$AUTH" "$SEL" "$REST" >/dev/null 2>&1; then
  fail "ws-ticket appears to be persisted"; else pass "ws-ticket never persisted"; fi

echo "== [7] Persistence pure helpers (key derivation + cookie merge) =="
python3 - <<'PY'
import hashlib, re, sys

ok = True
def check(desc, got, want):
    global ok
    if got == want: print(f"  PASS: {desc}")
    else: ok = False; print(f"  FAIL: {desc}  got={got!r} want={want!r}")

NAMES = [
 "__Host-hermes_session_at","__Secure-hermes_session_at","hermes_session_at",
 "__Host-hermes_session_rt","__Secure-hermes_session_rt","hermes_session_rt",
 "__Host-hermes_session_provider","__Secure-hermes_session_provider","hermes_session_provider",
]

def strip_prefix(name):
    for p in ("__Host-", "__Secure-"):
        if name.startswith(p):
            return name[len(p):]
    return name

# mirrors HermesRemoteAuth.ExtractSessionCookies
def extract(header):
    built = ""
    for name in NAMES:
        m = re.search(r"(?:^|[,;\s])" + re.escape(name) + r"=([^;,\s]+)", header or "")
        if not m: continue
        base = strip_prefix(name)
        if re.search(r"(?:^|[;\s])(?:__Host-|__Secure-)?" + re.escape(base) + r"=", built):
            continue
        seg = name + "=" + m.group(1)
        built = seg if not built else built + "; " + seg
    return built or None

# mirrors HermesRemoteAuth.NormalizeBaseUrl
def normalize_base_url(raw):
    if raw is None or raw.strip() == "":
        return None
    return raw.strip().rstrip("/") or None

# mirrors HermesRemoteAuth.BuildSessionSecretKey
def build_session_secret_key(provider_id, raw_base_url):
    normalized = normalize_base_url(raw_base_url)
    identity = (provider_id or "").strip() + "|" + (normalized.lower() if normalized else "")
    return "hermes_session_" + hashlib.sha256(identity.encode("utf-8")).hexdigest()[:32]

# mirrors HermesRemoteAuth.MergeCookieHeaders
def parse_pairs(header):
    names, values = [], []
    for part in (header or "").split(";"):
        part = part.strip()
        if not part: continue
        eq = part.find("=")
        if eq <= 0 or eq == len(part) - 1: continue
        names.append(part[:eq].strip()); values.append(part[eq+1:].strip())
    return names, values

def merge_cookie_headers(existing, incoming_header):
    incoming = extract(incoming_header)
    if not incoming: return existing
    if not existing: return incoming
    names, values = parse_pairs(existing)
    fresh_names, fresh_values = parse_pairs(incoming)
    for fn, fv in zip(fresh_names, fresh_values):
        base = strip_prefix(fn)
        idx = next((j for j, n in enumerate(names) if strip_prefix(n) == base), -1)
        if idx >= 0:
            names[idx], values[idx] = fn, fv
        else:
            names.append(fn); values.append(fv)
    return "; ".join(f"{n}={v}" for n, v in zip(names, values))

# --- key derivation: stable, scoped, non-colliding, opaque ---
k1 = build_session_secret_key("prov-1", "https://gateway.example")
check("key shape (prefix + 32 hex)", bool(re.fullmatch(r"hermes_session_[0-9a-f]{32}", k1)), True)
check("key stable across calls", build_session_secret_key("prov-1", "https://gateway.example"), k1)
check("trailing slash normalized", build_session_secret_key("prov-1", "https://gateway.example/"), k1)
check("host case normalized", build_session_secret_key("prov-1", "https://Gateway.Example"), k1)
check("whitespace normalized", build_session_secret_key(" prov-1 ", "  https://gateway.example  "), k1)
check("different provider id -> different key",
      build_session_secret_key("prov-2", "https://gateway.example") != k1, True)
check("different host -> different key",
      build_session_secret_key("prov-1", "https://other.example") != k1, True)
check("different scheme -> different key",
      build_session_secret_key("prov-1", "http://gateway.example") != k1, True)
check("host not readable in key", "gateway.example" in k1, False)
check("empty identity still keyed", bool(re.fullmatch(r"hermes_session_[0-9a-f]{32}", build_session_secret_key(None, None))), True)

# --- persisted bundle survives a round trip through extraction (restore path) ---
stored = "hermes_session_at=at.tok-1; hermes_session_rt=rt.tok-2; hermes_session_provider=nous"
check("restore round-trip", extract(stored), stored)
check("restore rejects junk payload", extract("garbage-not-a-cookie"), None)
check("restore rejects empty payload", extract(""), None)

# --- refresh merge: rotated values replace in place, untouched ones are kept ---
check("rotated access+refresh replace in place",
      merge_cookie_headers(stored,
          "hermes_session_at=at.NEW; Path=/; HttpOnly; Secure, hermes_session_rt=rt.NEW; Path=/; HttpOnly"),
      "hermes_session_at=at.NEW; hermes_session_rt=rt.NEW; hermes_session_provider=nous")
check("partial refresh keeps unmentioned cookies",
      merge_cookie_headers(stored, "hermes_session_rt=rt.ONLY; Path=/"),
      "hermes_session_at=at.tok-1; hermes_session_rt=rt.ONLY; hermes_session_provider=nous")
check("prefixed variant replaces bare form (not appended)",
      merge_cookie_headers("hermes_session_at=OLD", "__Host-hermes_session_at=NEW; Secure; HttpOnly"),
      "__Host-hermes_session_at=NEW")
check("new cookie appended",
      merge_cookie_headers("hermes_session_at=A", "hermes_session_rt=R; Path=/"),
      "hermes_session_at=A; hermes_session_rt=R")
check("no session cookies in response -> unchanged",
      merge_cookie_headers(stored, "csrftoken=xyz; Path=/"), stored)
check("empty Set-Cookie -> unchanged", merge_cookie_headers(stored, ""), stored)

sys.exit(0 if ok else 1)
PY
[ $? -eq 0 ] || FAIL=1

echo
if [ "$FAIL" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "CHECKS FAILED"; fi
exit $FAIL
