// Mímisbrunnr Console — browse view (Phase P1).
//
// The filter bar maps 1:1 onto the QueryMemories request (LADR-06): controls
// are translated into the request, and the returned rows are never re-filtered
// client-side. Empty / unreachable / timed-out / error render as distinct
// states (NFR-05). A result set equal to the limit surfaces a narrow-your-
// filters prompt rather than silently truncating or paging (no cursor).
import { postQuery, getInitiatives, getProductNames } from './api.js';
import { esc, shortUuid, fmtInstant, plural, fmtValidity } from './util.js';
import { openDetail, maybeOpenFromUrl } from './detail.js';
import { resolveKind, setQueryProductName, onViewContext } from './view-context.js';

const $ = (selector) => document.querySelector(selector);

const els = {
  results: $('#browse-results'),
  tableWrap: $('#browse-table-wrap'),
  table: $('#browse-table'),
  body: $('#browse-body'),
  state: $('#browse-state'),
  chips: $('#active-filters'),
  query: $('#f-query'),
  kind: $('#f-kind'),
  status: $('#f-status'),
  initiative: $('#f-initiative'),
  repo: $('#f-repo'),
  product: $('#f-product'),
  facet: $('#f-facet'),
  facetAdd: $('#f-facet-add'),
  facetList: $('#f-facet-list'),
  tag: $('#f-tag'),
  tagAdd: $('#f-tag-add'),
  tagList: $('#f-tag-list'),
  provider: $('#f-ticket-provider'),
  key: $('#f-ticket-key'),
  asof: $('#f-asof'),
  limit: $('#f-limit'),
  includeProposed: $('#f-include-proposed'),
  apply: $('#apply-filters'),
  clearAll: $('#clear-all'),
  kindOptions: $('#kind-options'),
  statusOptions: $('#status-options'),
  facetOptions: $('#facet-options'),
  tagOptions: $('#tag-options'),
  repoOptions: $('#repo-options'),
  ticketSystemOptions: $('#ticket-system-options'),
};

const state = {
  query: '',
  facets: [],
  facetMatchMode: 'any',
  tags: [],
  kind: '',
  status: '',
  includeProposed: false,
  ticketProvider: '',
  ticketKey: '',
  initiativeName: '',
  repo: '',
  productName: '',
  groupUuid: '',
  validity: 'current',
  asOf: '',
  limit: 50,
};

let currentRows = [];
let requestSeq = 0;

function collectOptions() {
  const kinds = new Set();
  const statuses = new Set();
  const facets = new Set();
  const tags = new Set();
  const repos = new Set();
  const systems = new Set();
  for (const row of currentRows) {
    if (row.kind) kinds.add(row.kind);
    if (row.status) statuses.add(row.status);
    for (const f of row.facets || []) facets.add(f);
    for (const t of row.tags || []) tags.add(t);
    if (row.repository) repos.add(row.repository);
    for (const ticket of row.tickets || []) if (ticket.provider) systems.add(ticket.provider);
  }
  els.kindOptions.innerHTML = [...kinds].map((k) => `<option value="${esc(k)}">`).join('');
  els.statusOptions.innerHTML = [...statuses].map((s) => `<option value="${esc(s)}">`).join('');
  els.facetOptions.innerHTML = [...facets].map((f) => `<option value="${esc(f)}">`).join('');
  els.tagOptions.innerHTML = [...tags].map((t) => `<option value="${esc(t)}">`).join('');
  // Suggestions only: the repo and System inputs stay free text, so a repository or a
  // tracker this corpus has never seen is still typeable.
  els.repoOptions.innerHTML = [...repos].sort().map((r) => `<option value="${esc(r)}">`).join('');
  const seeded = [...els.ticketSystemOptions.querySelectorAll('option')].map((option) => option.value);
  els.ticketSystemOptions.innerHTML = [...new Set([...seeded, ...systems])]
    .sort()
    .map((s) => `<option value="${esc(s)}">`).join('');
}

function chipList(container, items, label, onRemove) {
  container.innerHTML = items.map((item, index) => (
    `<span class="chip">${esc(label)}: ${esc(item)}` +
    `<button type="button" class="chip-x" data-index="${index}" aria-label="Remove ${esc(label)} ${esc(item)}">×</button></span>`
  )).join('');
  container.querySelectorAll('.chip-x').forEach((button) => {
    button.addEventListener('click', () => onRemove(Number(button.dataset.index)));
  });
}

function renderChips() {
  const chips = [];
  if (state.query) chips.push({ label: 'Query', value: state.query, remove: () => { state.query = ''; els.query.value = ''; } });
  if (state.facets.length) chips.push({ label: `Facets (${state.facetMatchMode})`, value: state.facets.join(', '), remove: () => { state.facets = []; renderListChips(); } });
  if (state.tags.length) chips.push({ label: 'Tags', value: state.tags.join(', '), remove: () => { state.tags = []; renderListChips(); } });
  if (state.kind) chips.push({ label: 'Kind', value: state.kind, remove: () => { state.kind = ''; els.kind.value = ''; } });
  if (state.status) chips.push({ label: 'Status', value: state.status, remove: () => { state.status = ''; els.status.value = ''; } });
  if (state.includeProposed) chips.push({ label: 'Proposed', value: 'included', remove: () => { state.includeProposed = false; els.includeProposed.checked = false; } });
  if (state.ticketProvider && state.ticketKey) chips.push({ label: 'Ticket', value: `${state.ticketProvider}:${state.ticketKey}`, remove: () => { state.ticketProvider = ''; state.ticketKey = ''; els.provider.value = ''; els.key.value = ''; } });
  if (state.initiativeName) chips.push({ label: 'Initiative', value: state.initiativeName, remove: () => { state.initiativeName = ''; els.initiative.value = ''; } });
  if (state.repo) chips.push({ label: 'Repo', value: state.repo, remove: () => { state.repo = ''; els.repo.value = ''; } });
  if (state.productName) chips.push({ label: 'Product name', value: state.productName, remove: () => { state.productName = ''; els.product.value = ''; } });
  if (state.groupUuid) chips.push({ label: 'Group', value: state.groupUuid, remove: () => { state.groupUuid = ''; } });
  if (state.validity === 'asof' && state.asOf) chips.push({ label: 'As of', value: state.asOf, remove: () => { state.validity = 'current'; state.asOf = ''; els.asof.value = ''; els.asof.hidden = true; document.querySelector('input[name="validity"][value="current"]').checked = true; } });

  els.chips.innerHTML = chips.map((c, i) => (
    `<span class="chip" role="listitem">${esc(c.label)}: ${esc(c.value)}` +
    `<button type="button" class="chip-x" data-index="${i}" aria-label="Remove ${esc(c.label)} ${esc(c.value)}">×</button></span>`
  )).join('');
  els.chips.querySelectorAll('.chip-x').forEach((button) => {
    button.addEventListener('click', () => {
      chips[Number(button.dataset.index)].remove();
      refresh();
    });
  });
}

function updateState() {
  state.query = els.query.value.trim();
  state.kind = els.kind.value.trim();
  state.status = els.status.value.trim();
  state.repo = els.repo.value.trim();
  state.initiativeName = els.initiative.value;
  state.productName = els.product.value;
  state.ticketProvider = els.provider.value.trim();
  state.ticketKey = els.key.value.trim();
  state.includeProposed = els.includeProposed.checked;
  state.limit = Number(els.limit.value);
  state.asOf = els.asof.value;
  const validity = document.querySelector('input[name="validity"]:checked');
  state.validity = validity ? validity.value : 'current';
  if (state.validity === 'asof') els.asof.hidden = false;
}

function buildRequest() {
  const request = { currentOnly: true, limit: state.limit };
  if (state.query) request.query = state.query;
  if (state.facets.length) { request.facets = [...state.facets]; request.facetMatchMode = state.facetMatchMode; }
  if (state.tags.length) request.tags = [...state.tags];
  const kind = resolveKind(state.kind);
  if (kind) request.kind = kind;
  if (state.status) request.status = state.status;
  if (state.includeProposed) request.includeProposed = true;
  if (state.ticketProvider && state.ticketKey) { request.ticketProvider = state.ticketProvider; request.ticketKey = state.ticketKey; }
  if (state.initiativeName) request.initiativeName = state.initiativeName;
  if (state.repo) request.repo = state.repo;
  if (state.productName) request.productName = state.productName;
  if (state.groupUuid) request.groupUuid = state.groupUuid;
  if (state.validity === 'asof' && state.asOf) {
    request.currentOnly = false;
    request.asOf = new Date(`${state.asOf}T00:00:00Z`).toISOString();
  }
  return request;
}

function setState(stateName, message) {
  els.tableWrap.hidden = true;
  els.state.hidden = false;
  els.state.dataset.state = stateName;
  els.state.textContent = message;
}

// A group accumulates tickets over time, so the honest rendering is the whole list:
// one ticket reads as `group · system:reference`, several read as a count rather than
// an arbitrary one standing in for the group.
function lineageOf(row) {
  const group = shortUuid(row.groupUuid);
  const tickets = (row.tickets || []).filter((ticket) => ticket.provider && ticket.key);
  if (tickets.length === 0) return group;
  if (tickets.length === 1) return `${group} · ${esc(tickets[0].provider)}:${esc(tickets[0].key)}`;
  return `${group} · ${tickets.length} tickets (${tickets.map((t) => esc(t.provider)).join(', ')})`;
}

function renderTable() {
  els.state.hidden = true;
  els.tableWrap.hidden = false;
  els.body.innerHTML = currentRows.map((row, index) => {
    const lineage = lineageOf(row);
    return `<tr class="browse-row" data-index="${index}" tabindex="0" role="row">
      <td>${esc(row.name)}</td>
      <td>${esc(row.kind)}</td>
      <td>${esc(row.status)}</td>
      <td>v${row.version ?? ''}${row.isCurrent ? ' · current' : ''}</td>
      <td>${esc(fmtValidity(row))}</td>
      <td>${esc(row.scopeDimension || '')}${row.scopeIdentifier ? ` · ${esc(row.scopeIdentifier)}` : ''}</td>
      <td>${lineage}<button type="button" class="pin" data-index="${index}" title="Narrow to this group">pin group</button></td>
      <td>${fmtInstant(row.createdOn)}</td>
    </tr>`;
  }).join('');
  els.body.querySelectorAll('.browse-row').forEach((row) => {
    const index = Number(row.dataset.index);
    row.addEventListener('click', () => selectRow(index));
    row.addEventListener('keydown', (event) => {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        const next = event.key === 'ArrowDown' ? index + 1 : index - 1;
        if (currentRows[next]) selectRow(next);
      }
    });
  });
  els.body.querySelectorAll('.pin').forEach((button) => {
    button.addEventListener('click', (event) => {
      event.stopPropagation();
      state.groupUuid = currentRows[Number(button.dataset.index)].groupUuid;
      refresh();
    });
  });
  collectOptions();
}

function selectRow(index) {
  els.body.querySelectorAll('.browse-row').forEach((row) => {
    const selected = Number(row.dataset.index) === index;
    row.classList.toggle('is-selected', selected);
    if (selected) {
      row.setAttribute('aria-selected', 'true');
      // Arrow-key walks must keep going from the new row (focus) and keep it visible (scroll);
      // a click lands focus here harmlessly.
      row.focus({ preventScroll: true });
      row.scrollIntoView({ block: 'nearest' });
    } else row.removeAttribute('aria-selected');
  });
  openDetail(currentRows[index]);
}

function atLimitPrompt(count) {
  return `Showing ${count} ${plural(count, 'memory', 'memories')} — narrow your filters (ticket / initiative / repo / facet / kind).`;
}

function renderResult(result) {
  els.results.querySelector('.limit-note')?.remove();
  if (result.timedOut) { setState('timed-out', 'The read request timed out. Try again.'); return; }
  if (result.unreachable) { setState('unreachable', 'The Console cannot reach the API. Is the AppHost stack running?'); return; }
  if (!result.ok) {
    // An unregistered product is a 404 from the query handler; its own wording names the fix, so
    // it is shown instead of a bare status code that would read as "no memories".
    const detail = result.data && (result.data.detail || result.data.title);
    setState('error', detail ? String(detail) : `The API returned HTTP ${result.status}.`);
    return;
  }
  const items = (result.data && Array.isArray(result.data.items)) ? result.data.items : [];
  currentRows = items;
  if (items.length === 0) { setState('empty', 'No memories match the current filters.'); return; }
  renderTable();
  if (items.length === state.limit) {
    const note = document.createElement('div');
    note.className = 'limit-note';
    note.textContent = atLimitPrompt(items.length);
    els.results.appendChild(note);
  }
}

let reloadTimer = null;
function scheduleReload(delay) {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(refresh, delay);
}

async function refresh() {
  updateState();
  renderChips();
  const seq = ++requestSeq;
  els.results.querySelector('.limit-note')?.remove();
  setState('loading', 'Loading memories…');
  const request = buildRequest();
  // The product this query carries is what a generated import command must repeat, so it is
  // recorded before the response — an unregistered product answers 404 and the error state
  // below says so rather than reading as "nothing matched".
  setQueryProductName(request.productName || '');
  const result = await postQuery(request);
  if (seq !== requestSeq) return;
  renderResult(result);
}

let controlsWired = false;
let initiativesLoaded = false;
let productsLoaded = false;
let viewContextSubscribed = false;

function wireControls() {
  // Sign-out then sign-in re-runs `initBrowse`; wiring twice would double every
  // listener (two queries per Apply). Wire once for the page's lifetime.
  if (controlsWired) return;
  controlsWired = true;
  const changeReload = () => scheduleReload(0);
  els.query.addEventListener('input', () => scheduleReload(300));
  [els.kind, els.status, els.repo, els.provider, els.key].forEach((input) => {
    input.addEventListener('input', () => scheduleReload(300));
  });
  els.initiative.addEventListener('change', changeReload);
  els.product.addEventListener('change', changeReload);
  els.limit.addEventListener('change', changeReload);
  els.includeProposed.addEventListener('change', changeReload);
  document.querySelectorAll('input[name="validity"]').forEach((radio) => {
    radio.addEventListener('change', () => {
      els.asof.hidden = document.querySelector('input[name="validity"]:checked').value !== 'asof';
      changeReload();
    });
  });
  els.asof.addEventListener('change', changeReload);
  els.apply.addEventListener('click', changeReload);
  els.clearAll.addEventListener('click', clearAll);

  els.facetAdd.addEventListener('click', () => addToken(els.facet, state.facets, () => renderListChips()));
  els.facet.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') { event.preventDefault(); addToken(els.facet, state.facets, () => renderListChips()); }
  });
  els.tagAdd.addEventListener('click', () => addToken(els.tag, state.tags, () => renderListChips()));
  els.tag.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') { event.preventDefault(); addToken(els.tag, state.tags, () => renderListChips()); }
  });
  document.querySelectorAll('input[name="facet-mode"]').forEach((radio) => {
    radio.addEventListener('change', () => {
      state.facetMatchMode = document.querySelector('input[name="facet-mode"]:checked').value;
      changeReload();
    });
  });
}

function addToken(input, array, rerender) {
  const value = input.value.trim();
  if (!value || array.includes(value)) { input.value = ''; return; }
  array.push(value);
  input.value = '';
  rerender();
  scheduleReload(0);
}

function renderListChips() {
  chipList(els.facetList, state.facets, 'facet', (index) => { state.facets.splice(index, 1); renderListChips(); scheduleReload(0); });
  chipList(els.tagList, state.tags, 'tag', (index) => { state.tags.splice(index, 1); renderListChips(); scheduleReload(0); });
}

async function clearAll() {
  Object.assign(state, {
    query: '', facets: [], facetMatchMode: 'any', tags: [], kind: '', status: '',
    includeProposed: false, ticketProvider: '', ticketKey: '', initiativeName: '',
    repo: '', productName: '', groupUuid: '', validity: 'current', asOf: '', limit: 50,
  });
  els.query.value = ''; els.kind.value = ''; els.status.value = ''; els.repo.value = '';
  els.provider.value = ''; els.key.value = ''; els.includeProposed.checked = false;
  els.initiative.value = ''; els.product.value = '';
  els.limit.value = '50'; els.asof.value = ''; els.asof.hidden = true;
  els.facetList.innerHTML = ''; els.tagList.innerHTML = '';
  document.querySelector('input[name="validity"][value="current"]').checked = true;
  document.querySelector('input[name="facet-mode"][value="any"]').checked = true;
  await refresh();
}

// The product dropdown lists the registry, one option per registered product. A product with no
// initiatives or repos yet is still offered: the query answers an honest empty for it rather than
// silently matching everything.
async function loadProducts() {
  const result = await getProductNames();
  if (!result.ok || !Array.isArray(result.data?.items)) return;
  for (const product of result.data.items) {
    if (!product || !product.name) continue;
    const option = document.createElement('option');
    option.value = product.name;
    option.textContent = product.description
      ? `${product.name} — ${product.description}`
      : product.name;
    els.product.appendChild(option);
  }
}

export async function initBrowse() {
  wireControls();
  renderListChips();
  // Re-login re-runs initBrowse; populate the initiative and product lists once so options
  // neither duplicate nor reset a selection the practitioner already made.
  if (!initiativesLoaded) {
    const initiatives = await getInitiatives();
    if (initiatives.ok && Array.isArray(initiatives.data?.items)) {
      initiativesLoaded = true;
      for (const init of initiatives.data.items) {
        const option = document.createElement('option');
        option.value = init.name;
        option.textContent = init.name;
        els.initiative.appendChild(option);
      }
    }
  }
  if (!productsLoaded) {
    await loadProducts();
    productsLoaded = true;
  }
  // A kind-scope change is a new request, never a client-side trim (LADR-06). Subscribed once:
  // re-login re-runs initBrowse, and a second subscriber would issue two queries per change.
  if (!viewContextSubscribed) {
    viewContextSubscribed = true;
    onViewContext((reason) => { if (reason === 'kind') refresh(); });
  }
  await refresh();
  maybeOpenFromUrl(currentRows);
}

// Cross-pane re-entry: refresh rows and open the URL-selected memory, but
// never re-wire controls (initBrowse is for the first activation only).
export async function reopenFromUrl() {
  await refresh();
  maybeOpenFromUrl(currentRows);
}
