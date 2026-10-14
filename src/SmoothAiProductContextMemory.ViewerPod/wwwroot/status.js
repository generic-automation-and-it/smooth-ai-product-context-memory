// Mímisbrunnr Console — Status (recall feedback) view (Phase P1.6, BR-53).
//
// Both recall-feedback endpoints require date args — never-recalled takes
// `asOf`, miss-rate takes `from` + `to` — so the view surfaces date controls
// rather than assuming a default (omitting them 400s). Miss (a retrieval that
// returned nothing) and absence (a memory never retrieved) are kept distinct.
// The limit clamps to the recall-feedback endpoint's own bound (G5, not the
// browse cap of 200). The Console reads via the query path and never writes
// feedback itself (NFR-06).
import { getMissRate, getNeverRecalled } from './api.js';

const $ = (selector) => document.querySelector(selector);

const els = {
  missForm: $('#missrate-form'),
  missFrom: $('#missrate-from'),
  missTo: $('#missrate-to'),
  missState: $('#missrate-state'),
  neverForm: $('#neverrecalled-form'),
  neverAsOf: $('#neverrecalled-asof'),
  neverLimit: $('#neverrecalled-limit'),
  neverState: $('#neverrecalled-state'),
  neverList: $('#neverrecalled-list'),
};

function isoDateOffset(days) {
  const date = new Date();
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

function utcInstant(dateValue) {
  return `${dateValue}T00:00:00Z`;
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

function showState(el, name, message) {
  el.hidden = false;
  el.dataset.state = name;
  el.textContent = message;
}

function renderMissRate(result) {
  if (result.timedOut) { showState(els.missState, 'timed-out', 'The request timed out. Try again.'); return; }
  if (result.unreachable) { showState(els.missState, 'unreachable', 'The Console cannot reach the API.'); return; }
  if (!result.ok) { showState(els.missState, 'error', `The API returned HTTP ${result.status}.`); return; }
  const data = result.data || {};
  const retrievals = data.retrievals ?? '?';
  const misses = data.misses ?? '?';
  const rate = typeof data.missRate === 'number' ? `${(data.missRate * 100).toFixed(1)}%` : '?';
  els.missState.hidden = false;
  els.missState.dataset.state = 'ok';
  els.missState.textContent = `Retrievals: ${retrievals} · Misses: ${misses} · Miss rate: ${rate}`;
}

function renderNeverRecalled(result) {
  els.neverList.innerHTML = '';
  if (result.timedOut) { showState(els.neverState, 'timed-out', 'The request timed out. Try again.'); return; }
  if (result.unreachable) { showState(els.neverState, 'unreachable', 'The Console cannot reach the API.'); return; }
  if (!result.ok) { showState(els.neverState, 'error', `The API returned HTTP ${result.status}.`); return; }
  const items = (result.data && Array.isArray(result.data.items)) ? result.data.items : [];
  els.neverState.hidden = false;
  els.neverState.dataset.state = 'ok';
  if (items.length === 0) {
    els.neverState.textContent = 'No memories were never recalled as of that date.';
    return;
  }
  els.neverState.textContent = `${items.length} memor(ies) never recalled as of that date.`;
  els.neverList.innerHTML = items.map((item) => (
    `<li><code>${esc(item.memoryUuid)}</code> · captured ${esc(item.capturedOn)}</li>`
  )).join('');
}

function wireForm(form, handler) {
  form.addEventListener('submit', (event) => { event.preventDefault(); handler(); });
}

export function initStatus() {
  els.missFrom.value = isoDateOffset(-30);
  els.missTo.value = isoDateOffset(0);
  els.neverAsOf.value = isoDateOffset(0);

  wireForm(els.missForm, () => {
    els.missState.hidden = true;
    const resultPromise = getMissRate(utcInstant(els.missFrom.value), utcInstant(els.missTo.value));
    resultPromise.then(renderMissRate);
  });
  wireForm(els.neverForm, () => {
    els.neverState.hidden = true;
    els.neverList.innerHTML = '';
    const resultPromise = getNeverRecalled(utcInstant(els.neverAsOf.value), Number(els.neverLimit.value));
    resultPromise.then(renderNeverRecalled);
  });
}
