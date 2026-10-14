// Mímisbrunnr Console — read API client.
//
// All calls go to the same origin; Caddy injects the read token (LADR-02), so
// this module never sets an Authorization header. Every call distinguishes
// three failure modes: an HTTP error (non-2xx), a timeout, and a network
// failure — the browse view renders them as distinct states (NFR-05, P1.5).

const DEFAULT_TIMEOUT = 10_000;

async function requestJson(path, { method = 'GET', body, timeoutMs = DEFAULT_TIMEOUT } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(path, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: controller.signal,
    });
    const text = await response.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); } catch { /* non-JSON body */ }
    }
    return { ok: response.ok, status: response.status, data };
  } catch (error) {
    if (error && error.name === 'AbortError') return { timedOut: true };
    return { unreachable: true };
  } finally {
    clearTimeout(timer);
  }
}

export function postQuery(requestBody) {
  return requestJson('/api/context/query', { method: 'POST', body: requestBody });
}

export function getLabels() {
  return requestJson('/api/context/labels');
}

export function getInitiatives(status) {
  const query = status ? `?status=${encodeURIComponent(status)}` : '';
  return requestJson(`/api/context/initiatives${query}`);
}

export function getNeverRecalled(asOf, limit) {
  return requestJson(`/api/context/recall-feedback/never-recalled?asOf=${encodeURIComponent(asOf)}&limit=${limit}`);
}

export function getMissRate(from, to) {
  return requestJson(`/api/context/recall-feedback/miss-rate?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
}
