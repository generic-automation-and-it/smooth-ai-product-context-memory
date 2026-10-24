// Mímisbrunnr Console — read API client.
//
// All calls go to the same origin; the session's bearer token (auth.js) is
// attached to every request and Caddy forwards it to the Host, which validates
// it (HLD-009). Every call distinguishes three failure modes: an HTTP error
// (non-2xx), a timeout, and a network failure — the browse view renders them as
// distinct states (NFR-05, P1.5).
import { authHeaders } from './auth.js';

const DEFAULT_TIMEOUT = 10_000;

async function requestJson(path, { method = 'GET', body, timeoutMs = DEFAULT_TIMEOUT } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(path, {
      method,
      headers: { ...authHeaders(), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
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
    const response = await fetch(`/api/context/memories/${uuid}/versions/${version}/blob${query}`, {
      headers: authHeaders(),
      signal: controller.signal,
    });
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

// The product-name registry (graph-side: a product linked to its initiatives and repos).
// It is not the `product` scope dimension — a memory scoped to `product` is not
// automatically part of any named product. The dropdown lists registered products only.
export function getProductNames() {
  return requestJson('/api/context/product-names');
}

export function getNeverRecalled(asOf, limit) {
  return requestJson(`/api/context/recall-feedback/never-recalled?asOf=${encodeURIComponent(asOf)}&limit=${limit}`);
}

export function getMissRate(from, to) {
  return requestJson(`/api/context/recall-feedback/miss-rate?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
}

// Capability probe (HLD-009): which tier the presented credential grants. The
// console gates write surfaces on this; read-only tokens cannot write.
export function getCapabilities() {
  return requestJson('/api/context/capabilities');
}

// P4a: the review queue and the dedup preview. Same-origin; the session's
// bearer token is forwarded to the bridge, which forwards it to the Host.
// `limit` is the *query* cap (200); the bridge clamps it, so an over-cap value
// narrows to the cap instead of becoming a 400 the bridge reports as a 502.
export function getReviewQueue(limit = 200, kind = '') {
  const params = new URLSearchParams({ limit: String(limit) });
  if (kind) params.set('kind', kind);
  return requestJson(`/bridge/review-queue?${params.toString()}`);
}

export function postDedupPreview(requestBody) {
  return requestJson('/bridge/dedup-preview', { method: 'POST', body: requestBody });
}

// P4b: the write surface. Approve is two-phase — a dryrun digest, then a
// confirm with the fingerprint the digest returned (the human veto, P4b.2).
export function postApprove(requestBody) {
  return requestJson('/bridge/approve', { method: 'POST', body: requestBody });
}

export function postReject(requestBody) {
  return requestJson('/bridge/reject', { method: 'POST', body: requestBody });
}

export function postAmend(requestBody) {
  return requestJson('/bridge/amend', { method: 'POST', body: requestBody });
}
