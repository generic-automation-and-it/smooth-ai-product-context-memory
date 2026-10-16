// Mímisbrunnr Console — Review pane (Phase P4a, read-side).
//
// The queue is a store query for `Status = proposed` memories (LADR-10). It
// runs through the bridge, which forwards the session's bearer token. Each
// proposal renders subject, kind, statement, binding (ticket / repo /
// initiative / scope / tags), version + status, and an "evidence" frame
// (LADR-07). A "Conflicts" subsection re-runs `dedup.stage` on the selected
// proposal and shows the conflict-choice menu the capture path would offer —
// new version / new memory / skip — reproduced, not persisted. Approve and
// Reject are the P4b write surface: enabled only when the session carries the
// write token (HLD-009); a read-only session sees them disabled.
import { getReviewQueue, postDedupPreview, postApprove, postReject } from './api.js';
import { esc, shortUuid, fmtValidity } from './util.js';

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
const conflictSeqByUuid = new Map();

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
    return `<p class="review-no-conflict">No conflicts found.</p>`;
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
      <p class="review-conflict-outcome">${esc(outcomeLabel(match.outcome))}</p>
      <div class="review-conflict-actions" role="group" aria-label="Conflict choice ${index + 1}">
        <button type="button" class="review-choice" data-choice="new-version" data-uuid="${esc(existing.uuid || '')}" disabled>New version</button>
        <button type="button" class="review-choice" data-choice="new-memory" data-uuid="${esc(existing.uuid || '')}" disabled>New memory</button>
        <button type="button" class="review-choice" data-choice="skip" data-uuid="${esc(existing.uuid || '')}" disabled>Skip</button>
        <button type="button" class="review-open" data-uuid="${esc(existing.uuid || '')}">Open in Browse</button>
      </div>
    </li>`;
  }).join('');
}

function scopeOf(memory) {
  return memory && memory.scopeDimension
    ? `${memory.scopeDimension}${memory.scopeIdentifier ? `:${memory.scopeIdentifier}` : ''}`
    : '';
}

function renderRow(memory) {
  const conflicts = conflictsByUuid.get(memory.uuid) || null;
  const canWrite = document.body.dataset.capability === 'write';
  const conflictsState = conflicts
    ? (conflicts.error
        ? `<p class="review-error">Dedup preview failed: ${esc(conflicts.error)}.</p>`
        : `<ul class="review-conflicts" aria-label="Conflicts">${conflicts.proposals.map(choiceMenu).join('')}</ul>`)
    : `<p class="review-help">Click "Show conflicts" to re-run dedup on this proposal.</p>`;

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
      <button type="button" class="review-approve" data-uuid="${esc(memory.uuid)}" ${canWrite ? '' : 'disabled'}>Approve</button>
      <button type="button" class="review-reject" data-uuid="${esc(memory.uuid)}" ${canWrite ? '' : 'disabled'}>Reject</button>
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
  els.list.querySelectorAll('.review-conflicts-toggle').forEach((button) => {
    button.addEventListener('click', () => loadConflicts(button.dataset.uuid));
  });
  els.list.querySelectorAll('.review-open').forEach((button) => {
    button.addEventListener('click', () => openInBrowse(button.dataset.uuid));
  });
  els.list.querySelectorAll('.review-open-browse').forEach((button) => {
    button.addEventListener('click', () => openInBrowse(button.dataset.uuid));
  });
  els.list.querySelectorAll('.review-approve').forEach((button) => {
    button.addEventListener('click', () => approveProposal(button.dataset.uuid));
  });
  els.list.querySelectorAll('.review-reject').forEach((button) => {
    button.addEventListener('click', () => rejectProposal(button.dataset.uuid));
  });
}

function openInBrowse(uuid) {
  if (!uuid) return;
  // `pushState` so the cross-pane listener in app.js activates Browse and
  // triggers `reopenFromUrl` (rows + open detail). No page reload.
  const url = new URL(window.location.href);
  url.hash = 'browse';
  url.searchParams.set('uuid', uuid);
  window.history.pushState({}, '', url);
  window.dispatchEvent(new PopStateEvent('popstate'));
}

async function loadConflicts(uuid) {
  const seq = (conflictSeqByUuid.get(uuid) || 0) + 1;
  conflictSeqByUuid.set(uuid, seq);
  const memory = currentRows.find((row) => row.uuid === uuid);
  const scope = scopeOf(memory);
  conflictsByUuid.set(uuid, null);
  const result = await postDedupPreview({ uuid, scope });
  if (conflictSeqByUuid.get(uuid) !== seq) return;  // a newer request superseded this one
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

async function approveProposal(uuid) {
  const memory = currentRows.find((row) => row.uuid === uuid);
  const scope = scopeOf(memory);
  // First phase: the dryrun digest (the human veto point, P4b.2).
  const first = await postApprove({ uuid, scope });
  if (first.timedOut) { alert('The approve request timed out.'); return; }
  if (first.unreachable) { alert('Cannot reach the approve bridge.'); return; }
  if (!first.ok) { alert(`The store refused the digest (HTTP ${first.status}).`); return; }
  const digest = first.data;
  if (!digest || digest.status !== 'digest') { alert('Unexpected approve response.'); return; }
  const veto = window.confirm(
    `Approve this proposal? The write sets Status=approved.\n\n` +
    `Digest fingerprint: ${digest.digestFingerprint || '(none)'}\n` +
    `Confirm to write.`
  );
  if (!veto) return;
  // Second phase: confirm with the fingerprint the digest returned.
  const second = await postApprove({ uuid, scope, confirm: true, digestFingerprint: digest.digestFingerprint });
  if (second.timedOut) { alert('The approve confirm timed out.'); return; }
  if (second.unreachable) { alert('Cannot reach the approve bridge.'); return; }
  if (!second.ok) { alert(`Approve failed (HTTP ${second.status}).`); return; }
  refresh();
}

async function rejectProposal(uuid) {
  const reason = window.prompt('Reason for rejection (optional):', '') ?? '';
  const result = await postReject({ uuid, reason });
  if (result.timedOut) { alert('The reject request timed out.'); return; }
  if (result.unreachable) { alert('Cannot reach the reject bridge.'); return; }
  if (!result.ok) { alert(`Reject failed (HTTP ${result.status}).`); return; }
  refresh();
}

function renderResult(result) {
  if (result.timedOut) { setState('timed-out', 'The review queue request timed out. Try again.'); return; }
  if (result.unreachable) { setState('unreachable', 'The Console cannot reach the bridge. Is the AppHost stack running?'); return; }
  if (!result.ok) { setState('error', `The bridge returned HTTP ${result.status}.`); return; }
  const items = (result.data && Array.isArray(result.data.items)) ? result.data.items : [];
  currentRows = items;
  if (items.length === 0) { setState('empty', 'No proposed memories are awaiting review.'); return; }
  renderList();
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
