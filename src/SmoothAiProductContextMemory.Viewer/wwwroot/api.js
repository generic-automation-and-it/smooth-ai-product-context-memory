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

// The current version's cheap fields, by identity. What a graph hop needs to be labelled:
// a traversal returns uuid references for every hop but the endpoint, and the query endpoint
// has no uuid predicate. Cheap fields only — never the body.
export function getMemory(uuid, scope) {
  const query = scope ? `?scope=${encodeURIComponent(scope)}` : '';
  return requestJson(`/api/context/memories/${uuid}${query}`);
}

// Bounded traversal: `maxDepth` is required on the body (1–5), so the view always sends it —
// there is no server default and an omitted bound would be a bound the practitioner never chose.
export function postPaths(requestBody) {
  return requestJson('/api/context/paths', { method: 'POST', body: requestBody });
}

// Separate surface: practitioner-declared ticket hierarchy, with its own disclosure and cap flags.
export function postTicketPaths(requestBody) {
  return requestJson('/api/context/tickets/paths', { method: 'POST', body: requestBody });
}

export function getMemoryVersions(uuid, scope) {
  const query = scope ? `?scope=${encodeURIComponent(scope)}` : '';
  return requestJson(`/api/context/memories/${uuid}/versions${query}`);
}

export async function getMemoryBlob(uuid, version, scope) {
  const query = scope ? `?scope=${encodeURIComponent(scope)}` : '';
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT);
  try {
    const response = await fetch(`/api/context/memories/${uuid}/versions/${version}/blob${query}`, { signal: controller.signal });
    const text = await response.text();
    return { ok: response.ok, status: response.status, text };
  } catch (error) {
    if (error && error.name === 'AbortError') return { timedOut: true };
    return { unreachable: true };
  } finally {
    clearTimeout(timer);
  }
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
