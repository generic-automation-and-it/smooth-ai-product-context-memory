// Mímisbrunnr Console — Graph view (Phase P3, BR-50).
//
// The view renders exactly what the store walked: `/paths` returns path *chains*
// (PathResult = Hop[] ending in a CheapMemory endpoint), not a neighbourhood
// adjacency list, so nothing is invented here (LADR-05). Three rules hold it honest:
//
//   * Bounded. `maxDepth` is always sent (1–5, UI default 2) and the header states the depth,
//     because there is no server default — an omitted bound is a bound nobody chose.
//   * Narrowing is a new request. Relation / direction / kind / status / scope / limit map 1:1
//     onto FindPaths fields; nothing is trimmed out of a returned blob (LADR-06).
//   * Nothing is fetched in a burst. The root and each path endpoint arrive labelled; intermediate
//     hops are uuid references and are labelled one at a time, on click (NFR-05).
//
// The layout is a force simulation run to convergence and then frozen — a static picture, so
// reduced motion is honoured by construction and there is no animation loop to leak CPU.
import { postPaths, postTicketPaths, getMemory } from './api.js';
import { esc, shortUuid, plural } from './util.js';
import { selectedMemory } from './detail.js';

const $ = (selector) => document.querySelector(selector);

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const CANVAS_WIDTH = 960;
const CANVAS_HEIGHT = 520;
const LAYOUT_ITERATIONS = 150;
const MAX_RENDERED_NODES = 250;

// Colour is never the only encoding: every relation also gets its own dash pattern, a legend
// entry, and a row in the paths table. WCAG AA against the panel background.
const RELATION_STYLES = [
  { colour: '#2458b0', dash: '' },
  { colour: '#6f3ba6', dash: '7 3' },
  { colour: '#0d6b62', dash: '2 3' },
  { colour: '#8a5300', dash: '11 3 2 3' },
  { colour: '#9c2064', dash: '4 2 1 2' },
  { colour: '#3f5b12', dash: '1 3' },
];

const els = {
  form: $('#graph-form'),
  root: $('#graph-root'),
  useBrowse: $('#graph-use-browse'),
  depth: $('#graph-depth'),
  relation: $('#graph-relation'),
  relationOptions: $('#graph-relation-options'),
  direction: $('#graph-direction'),
  kind: $('#graph-kind'),
  status: $('#graph-status'),
  scope: $('#graph-scope'),
  limit: $('#graph-limit'),
  header: $('#graph-header'),
  state: $('#graph-state'),
  wrap: $('#graph-canvas-wrap'),
  legend: $('#graph-legend'),
  canvas: $('#graph-canvas'),
  edgeDetail: $('#graph-edge-detail'),
  pathsBody: $('#graph-paths-body'),
  ticketForm: $('#ticket-graph-form'),
  ticketProvider: $('#ticket-provider'),
  ticketKey: $('#ticket-key'),
  ticketDepth: $('#ticket-depth'),
  ticketPathLimit: $('#ticket-path-limit'),
  ticketMemoryLimit: $('#ticket-memory-limit'),
  ticketState: $('#ticket-graph-state'),
  ticketResult: $('#ticket-graph-result'),
  ticketFlags: $('#ticket-graph-flags'),
  ticketPathsBody: $('#ticket-graph-paths-body'),
  ticketItemsBody: $('#ticket-graph-items-body'),
};

const state = {
  rootUuid: '',
  rootLabel: '',
  labeledRoot: '',
  paths: [],
  relations: [],
  selectedEdge: null,
  graph: null,
  positions: null,
  truncated: false,
  labels: new Map(),
  seq: 0,
};

let ticketInFlight = false;

function showState(el, name, message) {
  el.hidden = false;
  el.dataset.state = name;
  el.textContent = message;
}

function hideState(el) {
  el.hidden = true;
  el.textContent = '';
  el.removeAttribute('data-state');
}

function failureMessage(result, subject) {
  if (result.timedOut) return `${subject} timed out. Try again.`;
  if (result.unreachable) return 'The Console cannot reach the API. Is the AppHost stack running?';
  return `${subject} failed — the API returned HTTP ${result.status}.`;
}

function truncate(value, max) {
  const text = String(value ?? '');
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

function relationStyle(relation) {
  const index = state.relations.indexOf(relation);
  return RELATION_STYLES[index < 0 ? 0 : index % RELATION_STYLES.length];
}

function hash(text) {
  let value = 2166136261;
  for (let i = 0; i < text.length; i += 1) {
    value ^= text.charCodeAt(i);
    value = Math.imul(value, 16777619);
  }
  return value >>> 0;
}

/**
 * A spring–repulsion layout run to a fixed number of iterations and then frozen. Deterministic
 * (positions are seeded from the uuid, not from Math.random), so the same paths always draw the
 * same picture and a screenshot is comparable between runs.
 */
function layout(nodes, edges) {
  const positions = new Map();
  for (const node of nodes) {
    if (node.id === state.rootUuid) {
      positions.set(node.id, { x: CANVAS_WIDTH / 2, y: CANVAS_HEIGHT / 2 });
      continue;
    }
    const seed = hash(node.id);
    const angle = ((seed % 3600) / 3600) * Math.PI * 2;
    const radius = 0.55 + (((seed >> 8) % 1000) / 1000) * 0.4;
    positions.set(node.id, {
      x: CANVAS_WIDTH / 2 + Math.cos(angle) * radius * CANVAS_WIDTH * 0.42,
      y: CANVAS_HEIGHT / 2 + Math.sin(angle) * radius * CANVAS_HEIGHT * 0.42,
    });
  }

  for (let iteration = 0; iteration < LAYOUT_ITERATIONS; iteration += 1) {
    const cooling = 1 - iteration / LAYOUT_ITERATIONS;
    const displacement = new Map(nodes.map((node) => [node.id, { dx: 0, dy: 0 }]));

    for (let i = 0; i < nodes.length; i += 1) {
      for (let j = i + 1; j < nodes.length; j += 1) {
        const a = positions.get(nodes[i].id);
        const b = positions.get(nodes[j].id);
        let dx = a.x - b.x;
        let dy = a.y - b.y;
        let distance = Math.hypot(dx, dy);
        if (distance < 1) { dx = ((i % 7) - 3) / 3; dy = ((j % 7) - 3) / 3; distance = Math.hypot(dx, dy) || 1; }
        const push = Math.min(46000 / (distance * distance), 26) * cooling;
        const ux = (dx / distance) * push;
        const uy = (dy / distance) * push;
        displacement.get(nodes[i].id).dx += ux;
        displacement.get(nodes[i].id).dy += uy;
        displacement.get(nodes[j].id).dx -= ux;
        displacement.get(nodes[j].id).dy -= uy;
      }
    }

    for (const edge of edges) {
      const a = positions.get(edge.source);
      const b = positions.get(edge.target);
      if (!a || !b) continue;
      const dx = b.x - a.x;
      const dy = b.y - a.y;
      const distance = Math.hypot(dx, dy) || 1;
      const pull = (distance - 175) * 0.025 * cooling;
      const ux = (dx / distance) * pull;
      const uy = (dy / distance) * pull;
      displacement.get(edge.source).dx += ux;
      displacement.get(edge.source).dy += uy;
      displacement.get(edge.target).dx -= ux;
      displacement.get(edge.target).dy -= uy;
    }

    for (const node of nodes) {
      if (node.id === state.rootUuid) continue;
      const point = positions.get(node.id);
      const delta = displacement.get(node.id);
      point.x = Math.min(Math.max(point.x + delta.dx, 40), CANVAS_WIDTH - 40);
      point.y = Math.min(Math.max(point.y + delta.dy, 30), CANVAS_HEIGHT - 30);
    }
  }

  return positions;
}

function buildGraph(paths) {
  const nodes = new Map();
  const edges = [];
  const relations = new Set();
  let truncated = false;

  // The render cap bounds the force simulation, not the data: a 200-path walk at depth 5 is a
  // thousand nodes, and an O(n²) layout at that size is a browser stall, not a graph.
  const node = (id, label) => {
    if (!id) return null;
    const existing = nodes.get(id);
    if (existing) {
      if (!existing.label && label) existing.label = label;
      return existing;
    }
    if (nodes.size >= MAX_RENDERED_NODES) { truncated = true; return null; }
    const created = { id, label: label || state.labels.get(id) || '', root: id === state.rootUuid };
    nodes.set(id, created);
    return created;
  };

  node(state.rootUuid, state.rootLabel);
  const seen = new Map();
  for (const path of paths) {
    for (const hop of path.hops || []) {
      if (!node(hop.sourceUuid, '') || !node(hop.targetUuid, '')) continue;
      relations.add(hop.relation);
      // The same hop appears in every path that traverses it, so edges are deduped by identity:
      // drawing one line per path occurrence is the hairball LADR-05 refuses. The path table keeps
      // every chain, so nothing is lost by collapsing the picture.
      const key = `${hop.sourceUuid}->${hop.targetUuid}|${hop.relation}`;
      const existing = seen.get(key);
      if (existing) { existing.paths += 1; continue; }
      const edge = {
        id: `${hop.sourceUuid}->${hop.targetUuid}#${seen.size}`,
        source: hop.sourceUuid,
        target: hop.targetUuid,
        relation: hop.relation,
        reason: hop.reason,
        paths: 1,
      };
      seen.set(key, edge);
      edges.push(edge);
    }
    if (path.endpoint?.uuid) node(path.endpoint.uuid, path.endpoint.name || '');
  }

  return { nodes: [...nodes.values()], edges, relations: [...relations].sort(), truncated };
}

function renderLegend(relations) {
  if (relations.length === 0) {
    els.legend.innerHTML = '<li class="legend-empty">No edge types in this traversal.</li>';
    return;
  }
  els.legend.innerHTML = relations.map((relation) => {
    const style = relationStyle(relation);
    const dash = style.dash ? `stroke-dasharray: ${style.dash}` : '';
    return `<li><span class="legend-swatch" style="border-top: 3px ${style.dash ? 'dashed' : 'solid'} ${style.colour}" aria-hidden="true"></span>` +
      `<span class="legend-colour" style="background:${style.colour}" aria-hidden="true"></span>${esc(relation)}</li>`;
  }).join('');
}

function renderPathsTable(paths) {
  if (paths.length === 0) {
    els.pathsBody.innerHTML = '<tr><td colspan="3">No paths.</td></tr>';
    return;
  }
  els.pathsBody.innerHTML = paths.map((path) => {
    const hops = path.hops || [];
    // The chain walks the hops: the first hop's source is the root, each hop's target is the next
    // node. Rebuilding it from the targets alone would repeat the root once per hop.
    const chain = [...(hops.length ? [hops[0].sourceUuid] : [state.rootUuid]), ...hops.map((hop) => hop.targetUuid)]
      .map((uuid) => esc(shortUuid(uuid)))
      .join(` <span class="chain-arrow" aria-hidden="true">→</span> `);
    const relations = hops.map((hop) => `${esc(hop.relation)}: ${esc(truncate(hop.reason, 60))}`).join(' · ');
    const endpoint = path.endpoint?.name
      ? `${esc(truncate(path.endpoint.name, 48))} <code>${esc(shortUuid(path.endpoint.uuid))}</code>`
      : esc(shortUuid(path.endpoint?.uuid));
    return `<tr><td>${path.depth}</td><td>${chain}<br><span class="chain-reasons">${relations}</span></td><td>${endpoint}</td></tr>`;
  }).join('');
}

function nodeLabel(node) {
  return node.label ? truncate(node.label, 30) : shortUuid(node.id);
}

function renderCanvas(graph) {
  const positions = state.positions || new Map();
  const edges = graph.edges.map((edge) => {
    const a = positions.get(edge.source);
    const b = positions.get(edge.target);
    if (!a || !b) return '';
    const style = relationStyle(edge.relation);
    const selected = state.selectedEdge === edge.id;
    const width = selected ? 3.5 : 2;
    const dash = style.dash ? ` stroke-dasharray="${style.dash}"` : '';
    const midpointX = (a.x + b.x) / 2;
    const midpointY = (a.y + b.y) / 2;
    const relationTag = graph.edges.length <= 12
      ? `<text x="${midpointX.toFixed(1)}" y="${(midpointY - 5).toFixed(1)}" class="edge-relation" text-anchor="middle">${esc(edge.relation)}</text>`
      : '';
    return `<g class="edge${selected ? ' is-selected' : ''}" data-edge="${esc(edge.id)}" tabindex="0" role="button"` +
      ` aria-label="${esc(edge.relation)} from ${esc(shortUuid(edge.source))} to ${esc(shortUuid(edge.target))}. Reason: ${esc(edge.reason)}. On ${plural(edge.paths, 'path', 'paths')}.">` +
      `<title>${esc(edge.relation)} — ${esc(edge.reason)}</title>` +
      `<line x1="${a.x.toFixed(1)}" y1="${a.y.toFixed(1)}" x2="${b.x.toFixed(1)}" y2="${b.y.toFixed(1)}"` +
      ` stroke="${style.colour}" stroke-width="${width}"${dash} />${relationTag}</g>`;
  }).join('');

  const nodes = graph.nodes.map((node) => {
    const point = positions.get(node.id);
    if (!point) return '';
    const labelled = Boolean(node.label);
    const radius = node.root ? 13 : 9;
    const fill = node.root ? '#2458b0' : labelled ? '#0d6b62' : '#ffffff';
    const stroke = node.root ? '#12336a' : labelled ? '#0a4a44' : '#55555c';
    const kind = node.root ? 'root' : labelled ? 'labelled memory' : 'unlabelled hop';
    // Labels sit above a node in the lower half and below one in the upper half, so two neighbours
    // rarely write over each other's subject.
    const below = point.y <= CANVAS_HEIGHT / 2;
    const labelY = below ? point.y + radius + 12 : point.y - radius - 6;
    return `<g class="node${node.root ? ' is-root' : ''}${labelled ? ' is-labelled' : ''}" data-node="${esc(node.id)}" tabindex="0" role="button"` +
      ` aria-label="${esc(kind)}: ${esc(node.label || shortUuid(node.id))}. ${labelled ? 'Labelled already.' : 'Activate to read its subject.'}">` +
      `<circle cx="${point.x.toFixed(1)}" cy="${point.y.toFixed(1)}" r="${radius}" fill="${fill}" stroke="${stroke}" stroke-width="1.5" />` +
      `<text x="${point.x.toFixed(1)}" y="${labelY.toFixed(1)}" class="node-label" text-anchor="middle">${esc(nodeLabel(node))}</text>` +
      `</g>`;
  }).join('');

  els.canvas.innerHTML = `<svg viewBox="0 0 ${CANVAS_WIDTH} ${CANVAS_HEIGHT}" role="group"` +
    ` aria-label="Path graph, depth at most ${esc(els.depth.value)}, ${graph.nodes.length} ${plural(graph.nodes.length, 'node', 'nodes')}, ${graph.edges.length} ${plural(graph.edges.length, 'edge', 'edges')}">` +
    `${edges}${nodes}</svg>`;

  els.canvas.querySelectorAll('.edge').forEach((element) => {
    const show = () => selectEdge(graph, element.dataset.edge);
    element.addEventListener('click', show);
    element.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); show(); }
    });
    element.addEventListener('mouseenter', show);
  });
  els.canvas.querySelectorAll('.node').forEach((element) => {
    const activate = () => labelNode(graph, element.dataset.node);
    element.addEventListener('click', activate);
    element.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); activate(); }
    });
  });
}

function draw() {
  const graph = buildGraph(state.paths);
  state.graph = graph;
  state.relations = graph.relations;
  state.truncated = graph.truncated;
  renderLegend(graph.relations);
  renderPathsTable(state.paths);
  state.positions = layout(graph.nodes, graph.edges);
  renderCanvas(graph);
}

function selectEdge(graph, edgeId) {
  state.selectedEdge = edgeId;
  const edge = graph.edges.find((candidate) => candidate.id === edgeId);
  els.edgeDetail.textContent = edge
    ? `${edge.relation}: ${edge.reason} (${shortUuid(edge.source)} → ${shortUuid(edge.target)}, on ${plural(edge.paths, 'path', 'paths')})`
    : '';
  renderCanvas(graph);
}

/**
 * Labels one hop, on request. The traversal hands back uuid references for intermediate hops and the
 * query endpoint cannot address a uuid, so this is the one read a hop needs — issued per click, never
 * for every node at render time (NFR-05).
 */
async function labelNode(graph, uuid) {
  const node = graph.nodes.find((candidate) => candidate.id === uuid);
  if (!node || node.label) return;
  els.edgeDetail.textContent = `Reading ${shortUuid(uuid)}…`;
  const result = await getMemory(uuid, els.scope.value.trim());
  if (result.timedOut) { els.edgeDetail.textContent = `Reading ${shortUuid(uuid)} timed out.`; return; }
  if (result.unreachable) { els.edgeDetail.textContent = 'The Console cannot reach the API.'; return; }
  if (!result.ok) {
    els.edgeDetail.textContent = `Reading ${shortUuid(uuid)} failed — HTTP ${result.status}. `
      + (result.status === 403 ? 'Name its scope dimension above to read it.' : '');
    return;
  }
  node.label = result.data?.item?.name || result.data?.item?.description || '';
  if (!node.label) {
    els.edgeDetail.textContent = `${shortUuid(uuid)} has no subject to show.`;
    return;
  }
  state.labels.set(uuid, node.label);
  draw();
  els.edgeDetail.textContent = `${shortUuid(uuid)} → ${node.label}`;
}

function buildRequest() {
  const request = { sourceUuid: state.rootUuid, maxDepth: Number(els.depth.value), limit: Number(els.limit.value) };
  if (els.relation.value.trim()) request.relation = els.relation.value.trim();
  if (els.direction.value) request.direction = els.direction.value;
  if (els.kind.value.trim()) request.kind = els.kind.value.trim();
  if (els.status.value.trim()) request.status = els.status.value.trim();
  if (els.scope.value.trim()) request.scopeDimension = els.scope.value.trim();
  return request;
}

function collectRelationOptions() {
  els.relationOptions.innerHTML = state.relations.map((relation) => `<option value="${esc(relation)}">`).join('');
}

function renderPaths(result) {
  if (result.timedOut) { hideState(els.header); showState(els.state, 'timed-out', 'The traversal timed out. Try a shallower depth.'); els.wrap.hidden = true; return; }
  if (result.unreachable) { showState(els.state, 'unreachable', failureMessage(result, 'The traversal')); els.wrap.hidden = true; return; }
  if (!result.ok) { showState(els.state, 'error', failureMessage(result, 'The traversal')); els.wrap.hidden = true; return; }

  const paths = (result.data && Array.isArray(result.data.paths)) ? result.data.paths : [];
  state.paths = paths;
  const limit = Number(els.limit.value);
  const bound = paths.length === limit
    ? ` · showing the first ${limit} ${plural(limit, 'path', 'paths')} — narrow by relation or direction`
    : '';

  const graph = buildGraph(paths);
  const nodeBound = graph.truncated
    ? ` · drawing the first ${MAX_RENDERED_NODES} ${plural(MAX_RENDERED_NODES, 'node', 'nodes')}`
    : '';

  if (paths.length === 0) {
    hideState(els.header);
    els.wrap.hidden = true;
    showState(els.state, 'empty',
      'No edges from this element. The graph is additive: it holds edges where they were captured, not everywhere.');
    return;
  }

  hideState(els.state);
  els.wrap.hidden = false;
  els.header.textContent = `depth ≤ ${els.depth.value} · ${paths.length} ${plural(paths.length, 'path', 'paths')} · ${graph.edges.length} ${plural(graph.edges.length, 'edge', 'edges')}${bound}${nodeBound}`;
  state.selectedEdge = null;
  els.edgeDetail.textContent = '';
  draw();
  collectRelationOptions();
}

async function runPaths() {
  const rootUuid = els.root.value.trim();
  if (!UUID_PATTERN.test(rootUuid)) {
    showState(els.state, 'error', 'Choose a root explicitly: paste a memory uuid, or use the memory open in Browse.');
    els.wrap.hidden = true;
    return;
  }

  const seq = ++state.seq;
  state.rootUuid = rootUuid;
  showState(els.state, 'loading', `Reading the root ${shortUuid(rootUuid)}…`);
  els.wrap.hidden = true;

  // The traversal never returns the root's own CheapMemory, so the label is one read — cached per
  // root, never re-fetched for every filter change.
  let root = { ok: true };
  if (state.labeledRoot !== rootUuid) {
    root = await getMemory(rootUuid, els.scope.value.trim());
    if (seq !== state.seq) return;
    if (root.ok) {
      state.labeledRoot = rootUuid;
      state.rootLabel = root.data?.item?.name || root.data?.item?.description || '';
    }
  }
  if (root.timedOut) { showState(els.state, 'timed-out', 'Reading the root timed out.'); return; }
  if (root.unreachable) { showState(els.state, 'unreachable', failureMessage(root, 'Reading the root')); return; }
  if (!root.ok) {
    showState(els.state, 'error', `Reading the root failed — HTTP ${root.status}. `
      + (root.status === 404 ? 'That uuid is not a memory the Console can read.' : ''));
    return;
  }

  const result = await postPaths(buildRequest());
  if (seq !== state.seq) return;
  renderPaths(result);
}

function ticketFailure(result) {
  if (result.timedOut) { showState(els.ticketState, 'timed-out', 'The ticket traversal timed out. Try a shallower depth.'); return; }
  if (result.unreachable) { showState(els.ticketState, 'unreachable', failureMessage(result, 'The ticket traversal')); return; }
  showState(els.ticketState, 'error', failureMessage(result, 'The ticket traversal'));
}

function renderTicketPaths(result) {
  const data = result.data || {};
  const disclosure = data.disclosure || {};
  const flags = [
    disclosure.depthLimitReached ? 'Some paths were cut at the depth bound.' : null,
    disclosure.pathLimitReached ? 'Some paths were cut at the path limit.' : null,
    disclosure.memoryLimitReached ? 'Some memories were cut at the memory limit.' : null,
  ].filter(Boolean);

  els.ticketFlags.innerHTML = [
    `<p class="disclosure">${esc(disclosure.hierarchyCoverage || '')}</p>`,
    flags.length ? `<p class="flag">${flags.map(esc).join(' ')}</p>`
      : '<p class="flag">No traversal bound was reached.</p>',
  ].join('');

  const paths = Array.isArray(data.paths) ? data.paths : [];
  els.ticketPathsBody.innerHTML = paths.length
    ? paths.flatMap((path) => (path.hops || []).map((hop) => (
      `<tr><td>${path.depth}</td>` +
      `<td>${esc(hop.parent?.provider)}:${esc(hop.parent?.key)}</td>` +
      `<td>${esc(hop.child?.provider)}:${esc(hop.child?.key)}</td>` +
      `<td>${esc(hop.reason)}</td></tr>`))).join('')
    : '<tr><td colspan="4">No declared hierarchy under this anchor.</td></tr>';

  const items = Array.isArray(data.items) ? data.items : [];
  els.ticketItemsBody.innerHTML = items.length
    ? items.map((item) => (
      `<tr><td>${esc(item.name)}</td><td>${esc(item.kind)}</td><td>${esc(item.status)}</td>` +
      `<td>${esc(item.scopeDimension || '')}</td></tr>`)).join('')
    : '<tr><td colspan="4">No memories on these paths.</td></tr>';
}

async function runTicketPaths() {
  const provider = els.ticketProvider.value.trim();
  const key = els.ticketKey.value.trim();
  if (!provider || !key) {
    showState(els.ticketState, 'error', 'A ticket needs both a provider and a key — they are one identity.');
    els.ticketResult.hidden = true;
    return;
  }
  if (ticketInFlight) return;
  ticketInFlight = true;
  els.ticketResult.hidden = true;
  showState(els.ticketState, 'loading', 'Walking the declared ticket hierarchy…');

  const request = {
    anchor: { provider, key },
    maxDepth: Number(els.ticketDepth.value),
    pathLimit: Number(els.ticketPathLimit.value),
    memoryLimit: Number(els.ticketMemoryLimit.value),
  };
  const result = await postTicketPaths(request);
  ticketInFlight = false;
  if (result.ok) {
    hideState(els.ticketState);
    els.ticketResult.hidden = false;
    renderTicketPaths(result);
    return;
  }
  ticketFailure(result);
}

export function initGraph() {
  els.form.addEventListener('submit', (event) => { event.preventDefault(); runPaths(); });
  els.useBrowse.addEventListener('click', () => {
    const memory = selectedMemory();
    if (!memory) {
      showState(els.state, 'error', 'No memory is open in Browse. Select a row there first.');
      return;
    }
    els.root.value = memory.uuid;
    runPaths();
  });
  [els.depth, els.direction, els.limit].forEach((control) => {
    control.addEventListener('change', () => { if (state.rootUuid) runPaths(); });
  });
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || !state.selectedEdge) return;
    state.selectedEdge = null;
    els.edgeDetail.textContent = '';
    if (state.graph) renderCanvas(state.graph);
  });
  els.ticketForm.addEventListener('submit', (event) => { event.preventDefault(); runTicketPaths(); });
}