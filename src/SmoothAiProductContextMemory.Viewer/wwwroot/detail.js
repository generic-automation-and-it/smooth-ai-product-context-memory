// Mímisbrunnr Console — detail/provenance pane (Phase P2, BR-48/BR-49).
//
// The pane is a reader, not a form: append-only, no edit/delete. Every render
// carries the LADR-07 evidence frame. The cheap list never carries the body —
// the blob is fetched only when the element (or a specific version) is selected
// (NFR-05). Both the versions and blob reads pass the element's scope dimension,
// or the scope rule hides the very dimensions this pane is showing.
import { getMemoryVersions, getMemoryBlob, getMemory, postPaths } from './api.js';
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
let linksSeq = 0;

// The mini Links section: one bounded depth-1 walk, so opening a row costs one
// extra request, never a burst. Depth-1 endpoints arrive labelled (they are the
// neighbour's own CheapMemory), so no per-node read is needed to draw. A peer is
// read only when the practitioner activates it — one request, like a graph hop.
const LINKS_LIMIT = 50;
const LINKS_RENDER_MAX = 24;

function chipRow(items, label) {
  if (!items || items.length === 0) return '';
  return `<div class="chip-group"><h3>${label}</h3><div class="chip-list">` +
    items.map((item) => `<span class="chip">${esc(item)}</span>`).join('') +
    '</div></div>';
}

const IMPORT_CLIENT = '.agents/skills/mimisbrunnr-kvasir-understanding/scripts/understanding_client.py';
const IMPORT_SLASH = '/mimisbrunnr-kvasir-understanding';
const KIND_UNDERSTANDING = 'understanding';
const TICKET_MATCH_REFERENCE = 'reference';

/**
 * The flags that load this memory's neighbourhood into a session: the kvasir `import` verb
 * (store → session), pre-filled with the bindings this memory actually carries.
 *
 * It is a copy string, not an execution — the Console holds a read or write *store* token and
 * has no session to inject into, so it can only hand the command over. Three rules keep it honest:
 * a flag is emitted only when the binding exists; `import` is `kind = understanding` by
 * default, so any other kind carries `--all` — otherwise the command would return nothing and
 * read as "the store has no context"; and only *owned* tickets are bound, because `--ticket`
 * resolves the group that owns the ticket — binding a ticket this memory merely references
 * would recall that other group's memories, not this one's neighbourhood.
 */
function importFlags(memory) {
  const tickets = memory.tickets || [];
  const owned = tickets.filter((ticket) => ticket.provider && ticket.key && ticket.match !== TICKET_MATCH_REFERENCE);
  const referenced = tickets.filter((ticket) => ticket.provider && ticket.key && ticket.match === TICKET_MATCH_REFERENCE);
  const flags = [];
  if (memory.kind !== KIND_UNDERSTANDING) flags.push('--all');
  if (owned.length) flags.push(`--ticket ${shellQuote(`${owned[0].provider}:${owned[0].key}`)}`);
  if (memory.initiativeName) flags.push(`--initiative ${shellQuote(memory.initiativeName)}`);
  if (memory.repository) flags.push(`--repository ${shellQuote(memory.repository)}`);
  const product = queryProductNameValue();
  if (product) flags.push(`--product-name ${shellQuote(product)}`);
  return { flags: flags.join(' '), ownedCount: owned.length, referencedCount: referenced.length };
}

function importCommand(memory) {
  const { flags, ownedCount, referencedCount } = importFlags(memory);
  return { command: `python3 -B ${IMPORT_CLIENT} import${flags ? ` ${flags}` : ''}`, ownedCount, referencedCount };
}

function slashCommand(memory) {
  const { flags, ownedCount, referencedCount } = importFlags(memory);
  return { command: `${IMPORT_SLASH} import${flags ? ` ${flags}` : ''}`, ownedCount, referencedCount };
}

function renderImportCommand(memory) {
  const { command, ownedCount, referencedCount } = importCommand(memory);
  const { command: slash } = slashCommand(memory);
  const notes = [
    'Copies a command; the Console cannot run a skill. Hand it to an agent or run it yourself.',
  ];
  if (memory.kind !== KIND_UNDERSTANDING) {
    notes.push(`\`import\` reads understandings only, so this carries \`--all\` for kind \`${memory.kind}\`.`);
  }
  if (ownedCount > 1) {
    notes.push(`The group owns ${ownedCount} tickets and \`--ticket\` takes one, so the first is bound.`);
  }
  if (referencedCount > 0) {
    notes.push(`${referencedCount} referenced ticket${referencedCount === 1 ? ' is' : 's are'} not bound: \`--ticket\` resolves the owning group, so a reference would recall another group's memories.`);
  }
  if (!queryProductNameValue()) {
    notes.push('No product was in the Browse query, so no \`--product-name\` is included.');
  }
  return `<section class="detail-section" data-import>
    <div class="body-head"><h3>Recall</h3>
      <span class="button-row"><button type="button" id="recall-copy">Copy slash</button>
      <button type="button" id="import-copy">Copy import command</button></span></div>
    <p class="import-note">Slash skill (terminal):</p>
    <pre class="command" id="recall-slash" tabindex="0">${esc(slash)}</pre>
    <p class="import-note">Python equivalent:</p>
    <pre class="command" id="import-command" tabindex="0">${esc(command)}</pre>
    <p class="import-note" id="import-status" role="status"></p>
    <ul class="import-notes">${notes.map((note) => `<li>${esc(note)}</li>`).join('')}</ul>
  </section>`;
}

function badgeRow(memory) {
  const badges = [
    esc(memory.kind),
    esc(memory.status),
    `confidence ${esc(memory.confidence)}`,
    `v${esc(memory.version)}${memory.isCurrent ? ' · current' : ''}`,
  ];
  if (memory.initiativeName) badges.push(`Initiative: ${esc(memory.initiativeName)}`);
  if (memory.repository) badges.push(`Repo: ${esc(memory.repository)}`);
  for (const ticket of memory.tickets || []) {
    if (!ticket.provider || !ticket.key) continue;
    const mark = ticket.match === TICKET_MATCH_REFERENCE ? ' (referenced)' : '';
    badges.push(`Ticket: ${esc(ticket.provider)}:${esc(ticket.key)}${mark}`);
  }
  if (memory.scopeDimension) {
    badges.push(`Scope: ${esc(memory.scopeDimension)}${memory.scopeIdentifier ? `:${esc(memory.scopeIdentifier)}` : ''}`);
  }
  if (memory.productMatch) badges.push(`Product: via ${esc(memory.productMatch)}`);
  if (memory.ticketMatch) badges.push(`Matched: ${esc(memory.ticketMatch)}`);
  if (memory.groupUuid) badges.push(`Group: ${esc(String(memory.groupUuid).slice(0, 8))}`);
  return `<div class="badges">${badges.map((badge) => `<span class="badge">${badge}</span>`).join('')}</div>`;
}

function chipRowOrNone(items, label, noneText) {
  const chips = (items || []).map((item) => `<span class="chip">${esc(item)}</span>`).join('');
  return `<div class="chip-group"><h3>${label}</h3>` +
    (chips ? `<div class="chip-list">${chips}</div>` : `<p class="import-note">${noneText}</p>`) +
    '</div>';
}

function bindingSection(memory) {
  const chips = [];
  chips.push(memory.initiativeName
    ? `<span class="chip">Initiative: ${esc(memory.initiativeName)}</span>`
    : '<span class="chip">Initiative: —</span>');
  chips.push(memory.repository
    ? `<span class="chip">Repo: ${esc(memory.repository)}</span>`
    : '<span class="chip">Repo: —</span>');
  const tickets = (memory.tickets || []).filter((ticket) => ticket.provider && ticket.key);
  if (tickets.length) {
    for (const ticket of tickets) {
      const mark = ticket.match === TICKET_MATCH_REFERENCE ? ' (referenced)' : '';
      chips.push(`<span class="chip">Ticket: ${esc(ticket.provider)}:${esc(ticket.key)}${mark}</span>`);
    }
  } else {
    chips.push('<span class="chip">Tickets: none bound</span>');
  }
  return `<section class="detail-section"><div class="chip-group"><h3>Binding</h3><div class="chip-list">${chips.join('')}</div></div></section>`;
}

function renderStatic(memory) {
  // One version may cite several source documents (e.g. the file it was
  // captured from plus the session it came out of) — grouped under a single
  // term so the label does not repeat per source.
  const sources = (memory.sources || []).map((source) => {
    const when = source.capturedAt ? ` · ${fmtInstant(source.capturedAt)}` : '';
    return `<dd>${esc(source.kind)}: ${esc(source.reference)}${when}</dd>`;
  }).join('');

  return `
    <h2 class="detail-subject">${esc(memory.name)}</h2>
    ${badgeRow(memory)}
    <section class="detail-section"><h3>Statement</h3><p class="statement">${esc(memory.statement)}</p></section>
    ${memory.contentSummary ? `<section class="detail-section"><h3>Summary</h3><p>${esc(memory.contentSummary)}</p></section>` : ''}
    <section class="detail-section">
      <h3>Provenance</h3>
      <dl class="provenance">
        ${sources ? `<dt>Sources</dt>${sources}` : ''}
        <dt>Scope</dt><dd>${esc(memory.scopeDimension || '—')}${memory.scopeIdentifier ? ` · ${esc(memory.scopeIdentifier)}` : ''}</dd>
        <dt>Initiative</dt><dd>${esc(memory.initiativeName || '—')}</dd>
        <dt>Repository</dt><dd>${esc(memory.repository || '—')}</dd>
        <dt>Group</dt><dd><code>${esc(memory.groupUuid)}</code></dd>
        <dt>Validity</dt><dd>${esc(fmtValidity(memory))}</dd>
      </dl>
    </section>
    ${bindingSection(memory)}
    <section class="detail-section"><div class="chip-groups">${chipRow(memory.facets, 'Facets')}${chipRowOrNone(memory.tags, 'Tags', 'No tags.')}</div></section>
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

function linksPlaceholder() {
  return `<section class="detail-section" data-links><h3>Links · depth 1</h3>` +
    `<p class="import-note">Reading direct links…</p></section>`;
}

function renderMiniGraph(memory, peers) {
  const width = 400;
  const height = 200;
  const cx = width / 2;
  const cy = height / 2;
  const shown = peers.slice(0, LINKS_RENDER_MAX);
  const hidden = peers.length - shown.length;
  const edges = shown.map((peer) => {
    const a = { x: cx, y: cy };
    const b = peer.point;
    return `<line x1="${a.x.toFixed(1)}" y1="${a.y.toFixed(1)}" x2="${b.x.toFixed(1)}" y2="${b.y.toFixed(1)}" stroke="#2458b0" stroke-width="1.5" />` +
      (shown.length <= 12 ? `<text x="${((a.x + b.x) / 2).toFixed(1)}" y="${((a.y + b.y) / 2 - 4).toFixed(1)}" class="mini-edge-relation" text-anchor="middle">${esc(peer.relation)}</text>` : '');
  }).join('');
  const nodes = shown.map((peer) => {
    const below = peer.point.y <= cy;
    const labelY = below ? peer.point.y + 7 + 11 : peer.point.y - 7 - 5;
    const labelled = Boolean(peer.label);
    return `<g class="node${labelled ? ' is-labelled' : ''}" data-peer="${esc(peer.uuid)}" tabindex="0" role="button"` +
      ` aria-label="${labelled ? 'Memory' : 'Unlabelled memory'} ${esc(peer.label || peer.uuid.slice(0, 8))}, ${esc(peer.relation)}. Activate to open it.">` +
      `<circle cx="${peer.point.x.toFixed(1)}" cy="${peer.point.y.toFixed(1)}" r="7" fill="${labelled ? '#0d6b62' : '#ffffff'}" stroke="${labelled ? '#0a4a44' : '#55555c'}" stroke-width="1.5" />` +
      `<text x="${peer.point.x.toFixed(1)}" y="${labelY.toFixed(1)}" class="mini-node-label" text-anchor="middle">${esc(peer.label ? truncateLabel(peer.label) : peer.uuid.slice(0, 8))}</text>` +
      `</g>`;
  }).join('');
  const center = `<g class="node is-root"><circle cx="${cx}" cy="${cy}" r="11" fill="#2458b0" stroke="#12336a" stroke-width="1.5" />` +
    `<text x="${cx}" y="${(cy + 26).toFixed(1)}" class="mini-node-label" text-anchor="middle">${esc(truncateLabel(memory.name || 'this memory'))}</text></g>`;
  return `<h3>Links · depth 1 (${peers.length})</h3>` +
    `<div class="mini-graph"><svg viewBox="0 0 ${width} ${height}" role="group" aria-label="Direct links of ${esc(memory.name || 'this memory')}: ${peers.length} neighbours">${edges}${center}${nodes}</svg></div>` +
    (hidden > 0 ? `<p class="import-note">Showing ${shown.length} of ${peers.length} — narrow with kind/scope in the Graph pane for the rest.</p>` : '') +
    `<p class="import-note">Activate a node to open it.</p><p class="import-note" data-peer-status role="status"></p>`;
}

function truncateLabel(value) {
  const text = String(value ?? '');
  return text.length > 22 ? `${text.slice(0, 21)}…` : text;
}

async function openPeer(section, uuid, scope) {
  const status = section.querySelector('[data-peer-status]');
  if (status) status.textContent = 'Reading…';
  const result = await getMemory(uuid, scope);
  if (!currentMemory) return;
  if (result.timedOut || result.unreachable || !result.ok || !result.data?.item) {
    if (status) status.textContent = `Could not open it${result.status ? ` (HTTP ${result.status})` : ''}.`;
    return;
  }
  openDetail(result.data.item);
}

async function loadLinks(memory) {
  const seq = ++linksSeq;
  // The memory's own dimension rides along, like the versions and blob reads —
  // a uuid alone is not authority for a hidden dimension.
  const result = await postPaths({
    sourceUuid: memory.uuid, maxDepth: 1, limit: LINKS_LIMIT, scopeDimension: memory.scopeDimension,
  });
  if (currentMemory?.uuid !== memory.uuid || seq !== linksSeq) return;
  const section = els.body.querySelector('.detail-section[data-links]');
  if (!section) return;
  if (result.timedOut || result.unreachable || !result.ok) {
    section.innerHTML = `<h3>Links · depth 1</h3><p class="import-note">Direct links unavailable` +
      `${result.status ? ` (HTTP ${result.status})` : ''} — the versions and body above are unaffected.</p>`;
    return;
  }
  const peers = [];
  const seen = new Set();
  for (const path of result.data?.paths || []) {
    const hop = (path.hops || [])[0];
    if (!hop || !hop.targetUuid || hop.targetUuid === memory.uuid || seen.has(hop.targetUuid)) continue;
    seen.add(hop.targetUuid);
    const endpoint = path.endpoint?.uuid === hop.targetUuid ? path.endpoint : null;
    peers.push({
      uuid: hop.targetUuid,
      label: endpoint?.name || '',
      relation: hop.relation || '',
      reason: hop.reason || '',
    });
  }
  if (peers.length === 0) {
    section.innerHTML = `<h3>Links · depth 1</h3><p class="import-note">No direct links — ` +
      `the graph only holds edges where they were captured.</p>`;
    return;
  }
  peers.forEach((peer, index) => {
    const angle = -Math.PI / 2 + (index / peers.slice(0, LINKS_RENDER_MAX).length) * Math.PI * 2;
    peer.point = { x: 200 + Math.cos(angle) * 148, y: 100 + Math.sin(angle) * 70 };
  });
  section.innerHTML = renderMiniGraph(memory, peers);
  section.querySelectorAll('[data-peer]').forEach((element) => {
    const activate = () => openPeer(section, element.dataset.peer, memory.scopeDimension);
    element.addEventListener('click', activate);
    element.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); activate(); }
    });
  });
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
    if (event.target.closest('#import-copy')) copyCommand('#import-command');
    if (event.target.closest('#recall-copy')) copyCommand('#recall-slash');
  });
}

/**
 * Copy the generated command. The async clipboard is unavailable outside a secure context — the
 * Console is loopback HTTP, which browsers treat as trustworthy, but a proxy reaching it by
 * hostname is not — so the fallback selects the text and says what to press, rather than
 * reporting a success that did not happen.
 */
async function copyCommand(selector) {
  const pre = els.body.querySelector(selector);
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
    // Recall sits directly under Versions: the version list says what exists, the recall
    // section says how to load this memory's neighbourhood back into a session.
    els.body.insertAdjacentHTML('beforeend', linksPlaceholder());
    els.body.insertAdjacentHTML('beforeend', renderImportCommand(opened));
    loadLinks(opened);
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
