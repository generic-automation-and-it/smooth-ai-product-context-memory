// Mímisbrunnr Console — Review pane (Phase P4a, read-side).
//
// The queue is a store query for `Status = proposed` memories (LADR-10). It
// runs through the bridge, which never holds a write token (NFR-02). Each
// proposal renders subject, kind, statement, binding (ticket / repo /
// initiative / scope / tags), version + status, and an "evidence" frame
// (LADR-07). A "Conflicts" subsection re-runs `dedup.stage` on the selected
// proposal and shows the conflict-choice menu the capture path would offer —
// new version / new memory / skip — reproduced, not persisted. Approve and
// Reject are P4b's work (gated on G2); they render disabled.
import { getReviewQueue, postDedupPreview } from './api.js';
import { esc, shortUuid, fmtInstant, fmtValidity, plural } from './util.js';

const $ = (selector) => document.querySelector(selector);

const els = {
  limit: $('#review-limit'),
  refresh: $('#review-refresh'),
  state: $('#review-state'),
  list: $('#review-list'),
};

let currentRows = [];
let requestSeq = 0;
const conflictsByUuid = new Map();

function setState(stateName, message) {
  els.list.hidden = true;
  els.state.hidden = false;
  els.state.dataset.state = stateName;
  els.state.textContent = message;
}

function bindingChips(memory) {
  const chips = [];
  if (memory.ticketProvider && memory.ticketKey) {
    chips.push(`Ticket: ${esc(memory.ticketProvider)}:${esc(memory.ticketKey)}`);
  }
  if (memory.repository) chips.push(`Repo: ${esc(memory.repository)}`);
  if (memory.initiativeName) chips.push(`Initiative: ${esc(memory.initiativeName)}`);
  if (memory.scopeDimension) {
    const ident = memory.scopeIdentifier ? `: ${esc(memory.scopeIdentifier)}` : '';
    chips.push(`Scope: ${esc(memory.scopeDimension)}${ident}`);
  }
  for (const tag of memory.tags || []) chips.push(`tag: ${esc(tag)}`);
  return chips.map((c) => `<span class="chip">${c}</span>`).join('');
}

function outcomeLabel(outcome) {
  switch (outcome) {
    case 'exact': return 'exact match';
    case 'proposed': return 'model: duplicate';
    case 'possible-duplicate': return 'lexical only (model disabled)';
    case 'none': return 'no duplicate';
    case 'unexamined': return 'unexamined (lexical query failed)';
    case 'unexamined (budget)': return 'unexamined (budget)';
    default: return outcome || 'unknown';
  }
}

function choiceMenu(proposal) {
  const matches = Array.isArray(proposal.matches) ? proposal.matches : [];
  if (!matches.length) {
    return `<p class="review-no-conflict">No conflicts found. Safe to keep as a new version.</p>`;
  }
  return matches.map((match, index) => {
    const existing = match.existing || {};
    return `<li class="review-conflict" role="listitem">
      <p class="review-conflict-summary">
        <strong>${esc(existing.subject || existing.uuid || '(unknown)')}</strong>
        <span class="review-conflict-kind">${esc(existing.kind || '')}</span>
        <span class="review-conflict-uuid">${shortUuid(existing.uuid)}</span>
      </p>
      <p class="review-conflict-statement">${esc(existing.statement || '')}</p>
      <p class="review-conflict-outcome">${esc(outcomeLabel(match.outcome || match.reusable ? 'proposed' : 'none'))}</p>
      <div class="review-conflict-actions" role="group" aria-label="Conflict choice ${index + 1}">
        <button type="button" class="review-choice" data-choice="new-version" data-uuid="${esc(existing.uuid || '')}" disabled>New version</button>
        <button type="button" class="review-choice" data-choice="new-memory" data-uuid="${esc(existing.uuid || '')}" disabled>New memory</button>
        <button type="button" class="review-choice" data-choice="skip" data-uuid="${esc(existing.uuid || '')}" disabled>Skip</button>
        <button type="button" class="review-open" data-uuid="${esc(existing.uuid || '')}">Open in Browse</button>
      </div>
    </li>`;
  }).join('');
}

function renderRow(memory) {
  const conflicts = conflictsByUuid.get(memory.uuid) || null;
  const conflictsState = conflicts
    ? (conflicts.error
        ? `<p class="review-error">Dedup preview failed: ${esc(conflicts.error)}.</p>`
        : `<ul class="review-conflicts" aria-label="Conflicts">${conflicts.proposals.map(choiceMenu).join('')}</ul>`)
    : `<p class="review-loading">Loading conflict preview…</p>`;

  return `<article class="review-card" data-uuid="${esc(memory.uuid)}" role="article">
    <header class="review-card-head">
      <h2 class="review-subject">${esc(memory.name || memory.subject || '')}</h2>
      <p class="review-meta">
        <span class="review-kind">${esc(memory.kind || '')}</span>
        <span class="review-status">${esc(memory.status || '')}</span>
        <span class="review-version">v${memory.version ?? ''}${memory.isCurrent ? ' · current' : ''}</span>
        <span class="review-validity">${esc(fmtValidity(memory))}</span>
        <span class="review-uuid">${shortUuid(memory.uuid)} · ${esc(memory.groupUuid || '')}</span>
      </p>
    </header>
    <p class="review-statement">${esc(memory.statement || memory.contentSummary || '')}</p>
    <div class="review-binding">${bindingChips(memory)}</div>
    <div class="review-card-actions" role="group" aria-label="Proposal actions">
      <button type="button" class="review-open-browse" data-uuid="${esc(memory.uuid)}">Open in Browse</button>
      <button type="button" class="review-conflicts-toggle" data-uuid="${esc(memory.uuid)}" aria-expanded="${conflicts ? 'true' : 'false'}">${conflicts ? 'Refresh conflicts' : 'Show conflicts'}</button>
      <button type="button" class="review-approve" data-uuid="${esc(memory.uuid)}" disabled>Approve (P4b)</button>
      <button type="button" class="review-reject" data-uuid="${esc(memory.uuid)}" disabled>Reject (P4b)</button>
    </div>
    <section class="review-conflicts-pane" aria-label="Conflict preview">
      ${conflictsState}
    </section>
  </article>`;
}

function renderList() {
  els.state.hidden = true;
  els.list.hidden = false;
  els.list.innerHTML = currentRows.map(renderRow).join('');
  els.list.querySelectorAll('.review-open-browse').forEach((button) => {
    button.addEventListener('click', () => {
      const uuid = button.dataset.uuid;
      window.location.hash = '#browse';
      window.location.search = `?uuid=${encodeURIComponent(uuid)}`;
    });
  });
  els.list.querySelectorAll('.review-conflicts-toggle').forEach((button) => {
    button.addEventListener('click', () => loadConflicts(button.dataset.uuid));
  });
  els.list.querySelectorAll('.review-open').forEach((button) => {
    button.addEventListener('click', () => {
      const uuid = button.dataset.uuid;
      if (!uuid) return;
      window.location.hash = '#browse';
      window.location.search = `?uuid=${encodeURIComponent(uuid)}`;
    });
  });
}

async function loadConflicts(uuid) {
  const memory = currentRows.find((row) => row.uuid === uuid);
  const scope = memory && memory.scopeDimension
    ? `${memory.scopeDimension}${memory.scopeIdentifier ? `:${memory.scopeIdentifier}` : ''}`
    : '';
  conflictsByUuid.set(uuid, null);
  const result = await postDedupPreview({ uuid, scope });
  if (result.timedOut) {
    conflictsByUuid.set(uuid, { error: 'timed out' });
  } else if (result.unreachable) {
    conflictsByUuid.set(uuid, { error: 'bridge unreachable' });
  } else if (!result.ok) {
    conflictsByUuid.set(uuid, { error: `HTTP ${result.status}` });
  } else {
    conflictsByUuid.set(uuid, { proposals: Array.isArray(result.data?.proposals) ? result.data.proposals : [] });
  }
  renderList();
}

function renderResult(result) {
  if (result.timedOut) { setState('timed-out', 'The review queue request timed out. Try again.'); return; }
  if (result.unreachable) { setState('unreachable', 'The Console cannot reach the bridge. Is the AppHost stack running?'); return; }
  if (!result.ok) { setState('error', `The bridge returned HTTP ${result.status}.`); return; }
  const items = (result.data && Array.isArray(result.data.items)) ? result.data.items : [];
  currentRows = items;
  if (items.length === 0) { setState('empty', 'No proposed memories are awaiting review.'); return; }
  renderList();
  // Lazy-load the conflict preview for each row, in order. Serialised so a
  // slow first request never overlaps a faster later one (the same sequence
  // guard P1 uses — see LADR-10 consequences).
  (async () => {
    for (const row of currentRows) {
      await loadConflicts(row.uuid);
    }
  })();
}

async function refresh() {
  const limit = Number(els.limit.value) || 500;
  const seq = ++requestSeq;
  setState('loading', 'Loading proposed memories…');
  const result = await getReviewQueue(limit);
  if (seq !== requestSeq) return;
  renderResult(result);
}

function wireControls() {
  els.refresh.addEventListener('click', () => refresh());
  els.limit.addEventListener('change', () => refresh());
}

export function initReview() {
  wireControls();
  refresh();
}
