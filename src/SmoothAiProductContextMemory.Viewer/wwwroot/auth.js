// Mímisbrunnr Console — session credential.
//
// The Console is credential-gated (HLD-009): the user enters a CONTEXT_TOKEN
// (the API read or write token) at login. It is held here and mirrored to
// sessionStorage so a reload keeps the session, but it never persists past the
// tab (no localStorage) and only ever travels to the same-origin API + bridge
// proxies, which forward it to the Host. XSS could read it, so the SPA escapes
// all echoed data (util.js) and a strict Content-Security-Policy is set.

const TOKEN_KEY = 'mimir-bearer-token';
let token = null;

export function setToken(value) {
  token = value;
  try { sessionStorage.setItem(TOKEN_KEY, value); } catch { /* private mode */ }
}

export function getToken() {
  if (token === null) {
    try { token = sessionStorage.getItem(TOKEN_KEY) || null; } catch { token = null; }
  }
  return token;
}

export function clearToken() {
  token = null;
  try { sessionStorage.removeItem(TOKEN_KEY); } catch { /* ignored */ }
}

export function isAuthed() {
  return Boolean(getToken());
}

export function authHeaders() {
  const value = getToken();
  return value ? { Authorization: `Bearer ${value}` } : {};
}
