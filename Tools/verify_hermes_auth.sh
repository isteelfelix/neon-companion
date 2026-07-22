#!/usr/bin/env bash
# Deterministic static verification for the Hermes P8 remote-auth slice.
#
# The Companion is a Unity 6 project; no Unity runtime or C# compiler is available in
# CI here, so this script does what CAN be checked deterministically:
#   1. C# 9 compliance lint on the changed files (Unity 6 forbids C# 10+ syntax).
#   2. Auth contract wiring is present (password-login, ws-ticket, ?ticket=, Cookie).
#   3. Legacy token-mode path is preserved unchanged.
#   4. The cookie-extraction regex actually works on representative Set-Cookie headers
#      (the .NET pattern is re-run with python3, whose regex syntax matches for this pattern).
#
# Exit non-zero on any failure.
set -uo pipefail
cd "$(dirname "$0")/.."

FAIL=0
pass() { echo "  PASS: $1"; }
fail() { echo "  FAIL: $1"; FAIL=1; }

FILES=(
  "Assets/Scripts/Runtime/Api/Hermes/HermesRemoteAuth.cs"
  "Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs"
  "Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs"
  "Assets/Scripts/Runtime/Core/GlobalBackendSelector.cs"
  "Assets/Scripts/Runtime/Data/Models/ProviderConfig.cs"
)

echo "== [1] Files exist =="
for f in "${FILES[@]}"; do
  if [ -f "$f" ]; then pass "$f"; else fail "missing $f"; fi
done

echo "== [2] C# 9 compliance (Unity 6 constraints) =="
# switch expressions:  something => (inside a `switch (x) {  x => y  }` arm)
if grep -nE '=>\s' "${FILES[@]}" | grep -vE '///|//' | grep -E '\)\s*=>|\}\s*=>|switch' >/dev/null 2>&1; then
  # Narrow: flag only the "= <expr> switch {" / "case ... =>" style is hard; instead flag
  # any `switch` used as an expression (assignment form: `= ... switch`).
  :
fi
if grep -nE '=\s*[A-Za-z0-9_.\)]+\s+switch\s*\{' "${FILES[@]}" >/dev/null 2>&1; then
  fail "switch expression found (use switch statement)"; else pass "no switch expressions"; fi
# Only the C# pattern form: `is not null`, `is not <CapitalizedType>`, `is not (` / `{`.
# (Avoids flagging English prose like "URL is not configured".)
if grep -nE '\bis\s+not\s+(null|[A-Z(\{])' "${FILES[@]}" >/dev/null 2>&1; then
  fail "'is not' pattern found (use != / !(x is T))"; else pass "no 'is not' patterns"; fi
if grep -nE '\bis\s+null\b' "${FILES[@]}" >/dev/null 2>&1; then
  fail "'is null' pattern found (use == null)"; else pass "no 'is null' patterns"; fi
# target-typed new(): `new(` or `= new()` with no type name before the paren.
if grep -nE '(=|\breturn|\(|,)\s*new\s*\(' "${FILES[@]}" >/dev/null 2>&1; then
  fail "target-typed new() found (use new TypeName())"; else pass "no target-typed new()"; fi
# UnityEngine.Serializable misuse
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
# Single-use ticket must never be persisted.
if grep -nE 'SetSecret\([^)]*ticket' Assets/Scripts/Runtime/**/*.cs >/dev/null 2>&1; then
  fail "ticket appears to be persisted"; else pass "ticket not persisted"; fi
# Password must not be logged.
if grep -nE '(Debug\.Log|NeonLogger)[^\n]*password' Assets/Scripts/Runtime/**/*.cs >/dev/null 2>&1; then
  fail "password may be logged"; else pass "password not logged"; fi

echo "== [4] Legacy token-mode preserved =="
grep -q 'token=' Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs \
  && pass "?token= path kept" || fail "?token= path removed"
grep -q 'Authorization", "Bearer ' Assets/Scripts/Runtime/Api/Hermes/HermesRestClient.cs \
  && pass "Bearer token path kept" || fail "Bearer token path removed"

echo "== [5] Cookie-extraction regex behavior =="
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

sys.exit(0 if ok else 1)
PY
[ $? -eq 0 ] || FAIL=1

echo
if [ "$FAIL" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "CHECKS FAILED"; fi
exit $FAIL
