// Mímisbrunnr Console — detail/provenance pane (Phase P2, BR-48/BR-49).
//
// The pane is a reader, not a form: append-only, no edit/delete. Every render
// carries the LADR-07 evidence frame. The cheap list never carries the body —
// the blob is fetched only when the element (or a specific version) is selected
// (NFR-05). Both the versions and blob reads pass the element's scope dimension,
// or the scope rule hides the very dimensions this pane is showing.
import { getMemoryVersions, getMemoryBlob } from './api.js';
import { esc, fmtInstant, plural, fmtValidity, shellQuote } from './util.js';
import { queryProductNameValue } from './view-context.js';

const $ = (selector) => document.querySelector(selector);

const els = {
  pane: $('#detail'),
  body: $('#detail-body'),
  close: $('#detail-close'),
};

let currentMemory = null;
let blobSeq = 0;

function chipRow(items, label) {
  if (!items || items.length === 0) return '';
  return `<div class="chip-group"><h3>${label}</h3><div class="chip-list">` +
    items.map((item) => `<span class="chip">${esc(item)}</span>`).join('') +
    '</div></div>';
}

const IMPORT_CLIENT = '.agents/skills/mimisbrunnr-kvasir-understanding/scripts/understanding_client.py';
const KIND_UNDERSTANDING = 'understanding';

/**
 * The command that loads this memory's neighbourhood into a session: the kvasir `import` verb
 * (store → session), pre-filled with the bindings this memory actually carries.
 *
 * It is a copy string, not an execution — the Console holds a read or write *store* token and
 * has no session to inject into, so it can only hand the command over. Two rules keep it honest:
 * a flag is emitted only when the binding exists, and `import` is `kind = understanding` by
 * default, so any other kind carries `--all` — otherwise the command would return nothing and
 * read as "the store has no context".
 */
function importCommand(memory) {
  const parts = [`python3 -B ${IMPORT_CLIENT} import`];
  if (memory.kind !== KIND_UNDERSTANDING) parts.push('--all');
  const tickets = (memory.tickets || []).filter((ticket) => ticket.provider && ticket.key);
  if (tickets.length) parts.push(`--ticket ${shellQuote(`${tickets[0].provider}:${tickets[0].key}`)}`);
  if (memory.initiativeName) parts.push(`--initiative ${shellQuote(memory.initiativeName)}`);
  if (memory.repository) parts.push(`--repository ${shellQuote(memory.repository)}`);
  const product = queryProductNameValue();
  if (product) parts.push(`--product-name ${shellQuote(product)}`);
  return { command: parts.join(' '), ticketCount: tickets.length };
}

function renderImportCommand(memory) {
  const { command, ticketCount } = importCommand(memory);
  const notes = [
    'Copies a command; the Console cannot run a skill. Hand it to an agent or run it yourself.',
  ];
  if (memory.kind !== KIND_UNDERSTANDING) {
    notes.push(`\`import\` reads understandings only, so this carries \`--all\` for kind \`${memory.kind}\`.`);
  }
  if (ticketCount > 1) {
    notes.push(`The group carries ${ticketCount} tickets and \`--ticket\` takes one, so the first is bound.`);
  }
  if (!queryProductNameValue()) {
    notes.push('No product was in the Browse query, so no \`--product-name\` is included.');
  }
  return `<section class="detail-section" data-import>
    <div class="body-head"><h3>Load into a session</h3>
      <button type="button" id="import-copy">Copy import command</button></div>
    <pre class="command" id="import-command" tabindex="0">${esc(command)}</pre>
    <p class="import-note" id="import-status" role="status"></p>
    <ul class="import-notes">${notes.map((note) => `<li>${esc(note)}</li>`).join('')}</ul>
  </section>`;
}

function renderStatic(memory) {
  const sources = (memory.sources || []).map((source) => {
    const when = source.capturedAt ? ` · ${fmtInstant(source.capturedAt)}` : '';
    return `<dt>Source</dt><dd>${esc(source.kind)}: ${esc(source.reference)}${when}</dd>`;
  }).join('');

  return `
    <h2 class="detail-subject">${esc(memory.name)}</h2>
    <div class="badges">
      <span class="badge">${esc(memory.kind)}</span>
      <span class="badge">${esc(memory.status)}</span>
      <span class="badge">confidence ${esc(memory.confidence)}</span>
      <span class="badge">v${esc(memory.version)}${memory.isCurrent ? ' · current' : ''}</span>
    </div>
    <section class="detail-section"><h3>Statement</h3><p class="statement">${esc(memory.statement)}</p></section>
    ${memory.contentSummary ? `<section class="detail-section"><h3>Summary</h3><p>${esc(memory.contentSummary)}</p></section>` : ''}
    <section class="detail-section">
      <h3>Provenance</h3>
      <dl class="provenance">
        ${sources}
        <dt>Scope</dt><dd>${esc(memory.scopeDimension || '—')}${memory.scopeIdentifier ? ` · ${esc(memory.scopeIdentifier)}` : ''}</dd>
        <dt>Group</dt><dd><code>${esc(memory.groupUuid)}</code></dd>
        <dt>Validity</dt><dd>${esc(fmtValidity(memory))}</dd>
      </dl>
    </section>
    <section class="detail-section"><div class="chip-groups">${chipRow(memory.facets, 'Facets')}${chipRow(memory.tags, 'Tags')}</div></section>
    ${renderImportCommand(memory)}
  `;
}

function renderVersions(versions) {
  const items = versions.map((v) => (
    `<li><button type="button" class="version-load" data-version="${v.version}">v${v.version}</button> ` +
    `<span class="version-state">${v.isCurrent ? 'current' : 'superseded'}</span> · ${fmtInstant(v.createdOn)}</li>`
  )).join('');
  return `<section class="detail-section"><h3>Versions (${plural(versions.length, 'version', 'versions')})</h3><ol class="version-list">${items}</ol></section>`;
}

function renderBody(text, version) {
  return `<section class="detail-section" data-body><div class="body-head"><h3>Body · v${version}</h3>` +
    `<button type="button" id="body-toggle">Hide body</button></div>` +
    `<pre id="detail-blob" class="blob">${esc(text)}</pre></section>`;
}

function bodyError(message) {
  return `<section class="detail-section" data-body><div class="body-head"><h3>Body</h3></div><p class="state" data-state="error">${esc(message)}</p></section>`;
}

function setBlobSection(html) {
  const existing = els.body.querySelector('.detail-section[data-body]');
  if (existing) existing.outerHTML = html;
  else els.body.insertAdjacentHTML('beforeend', html);
}

let detailWired = false;

export function initDetail() {
  // Re-login re-runs boot(); wire once so listeners do not double.
  if (detailWired) return;
  detailWired = true;
  els.close.addEventListener('click', closeDetail);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !els.pane.hidden) closeDetail();
  });
  els.body.addEventListener('click', (event) => {
    const versionButton = event.target.closest('.version-load');
    if (versionButton) {
      if (currentMemory) loadVersion(currentMemory, Number(versionButton.dataset.version));
      return;
    }
    const toggle = event.target.closest('#body-toggle');
    if (toggle) {
      const blob = els.body.querySelector('#detail-blob');
      if (blob) {
        blob.hidden = !blob.hidden;
        toggle.textContent = blob.hidden ? 'Show body' : 'Hide body';
      }
      return;
    }
    if (event.target.closest('#import-copy')) copyImportCommand();
  });
}

/**
 * Copy the generated command. The async clipboard is unavailable outside a secure context — the
 * Console is loopback HTTP, which browsers treat as trustworthy, but a proxy reaching it by
 * hostname is not — so the fallback selects the text and says what to press, rather than
 * reporting a success that did not happen.
 */
async function copyImportCommand() {
  const pre = els.body.querySelector('#import-command');
  const status = els.body.querySelector('#import-status');
  if (!pre || !status) return;
  if (navigator.clipboard && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(pre.textContent);
      status.textContent = 'Copied.';
      return;
    } catch {
      // Fall through to the selection fallback.
    }
  }
  const selection = window.getSelection();
  if (selection) {
    const range = document.createRange();
    range.selectNodeContents(pre);
    selection.removeAllRanges();
    selection.addRange(range);
  }
  status.textContent = 'The clipboard is unavailable in this context — the command is selected; copy it with your usual shortcut.';
}

async function loadVersion(memory, version) {
  const seq = ++blobSeq;
  const result = await getMemoryBlob(memory.uuid, version, memory.scopeDimension);
  if (currentMemory?.uuid !== memory.uuid || seq !== blobSeq) return;
  let html;
  if (result.timedOut) html = bodyError('The body request timed out.');
  else if (result.unreachable) html = bodyError('The Console cannot reach the API.');
  else if (!result.ok) html = bodyError(`HTTP ${result.status}`);
  else html = renderBody(result.text, version);
  setBlobSection(html);
}

export function openDetail(memory) {
  if (!memory) return;
  currentMemory = memory;
  els.pane.hidden = false;
  const url = new URL(window.location.href);
  url.searchParams.set('uuid', memory.uuid);
  history.replaceState(null, '', `${url.pathname}?${url.searchParams}`);

  els.body.innerHTML = renderStatic(memory);

  const opened = memory;
  getMemoryVersions(memory.uuid, memory.scopeDimension).then((versionsResult) => {
    if (currentMemory?.uuid !== opened.uuid) return;
    if (versionsResult.ok && Array.isArray(versionsResult.data?.items)) {
      els.body.insertAdjacentHTML('beforeend', renderVersions(versionsResult.data.items));
    }
    loadVersion(opened, opened.version);
  });
}

function closeDetail() {
  if (!currentMemory) return;
  currentMemory = null;
  els.pane.hidden = true;
  els.body.innerHTML = '';
  const url = new URL(window.location.href);
  url.searchParams.delete('uuid');
  history.replaceState(null, '', `${url.pathname}${url.search}`);
}

/**
 * The memory currently open in the detail pane, or null. The Graph reads this only when a practitioner
 * presses "use the memory open in Browse": selecting in Browse must never silently re-root the Graph,
 * so this is an accessor, not a subscription.
 */
export function selectedMemory() {
  return currentMemory;
}

export function maybeOpenFromUrl(rows) {
  const uuid = new URLSearchParams(window.location.search).get('uuid');
  if (!uuid) return;
  const row = rows.find((item) => item.uuid === uuid);
  if (row) openDetail(row);
}
