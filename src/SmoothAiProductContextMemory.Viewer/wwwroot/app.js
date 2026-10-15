// Mímisbrunnr Console shell — nav + view wiring.
//
// Browse is the default view (the first read view reachable through the
// loopback entry, P1). Status is initialised lazily on first activation so a
// browse-only session never issues the recall-feedback requests. The SPA sends
// no Authorization header; Caddy injects the read token (LADR-02).
import { initBrowse, reopenFromUrl } from './browse.js';
import { initStatus } from './status.js';
import { initDetail } from './detail.js';
import { initGraph } from './graph.js';
import { initReview } from './review.js';

let statusInitialized = false;
let graphInitialized = false;
let reviewInitialized = false;

const nav = document.querySelector('#nav');
const panes = document.querySelectorAll('.pane');

function activatePane(pane) {
  document.querySelectorAll('.nav-link').forEach((node) => {
    const active = node.dataset.pane === pane;
    node.classList.toggle('is-active', active);
    if (active) node.setAttribute('aria-current', 'page');
    else node.removeAttribute('aria-current');
  });
  panes.forEach((node) => {
    node.classList.toggle('is-active', node.id === `pane-${pane}`);
  });
  if (pane === 'status' && !statusInitialized) {
    statusInitialized = true;
    initStatus();
  }
  // The graph draws nothing until a root is chosen explicitly, so it initialises lazily like Status:
  // walking is a request the practitioner makes, not one a pane switch makes for them.
  if (pane === 'graph' && !graphInitialized) {
    graphInitialized = true;
    initGraph();
  }
  if (pane === 'review' && !reviewInitialized) {
    reviewInitialized = true;
    initReview();
  }
}

nav.addEventListener('click', (event) => {
  const link = event.target.closest('.nav-link');
  if (!link) return;
  event.preventDefault();
  const pane = link.dataset.pane;
  if (window.location.hash !== `#${pane}`) {
    // Setting the hash fires `hashchange`, whose handler calls
    // `activatePane`. Don't double-activate here.
    window.location.hash = pane;
  } else {
    activatePane(pane);
  }
});

// Cross-pane URL changes: a "Open in Browse" link from Review pushes
// `?uuid=…` onto the URL. The Browse pane is already initialised; refresh
// its rows and let `maybeOpenFromUrl` open the detail.
window.addEventListener('popstate', async () => {
  const uuid = new URLSearchParams(window.location.search).get('uuid');
  if (!uuid) return;
  const browsePane = document.getElementById('pane-browse');
  if (browsePane && !browsePane.classList.contains('is-active')) {
    activatePane('browse');
  }
  await reopenFromUrl();
});

window.addEventListener('hashchange', () => {
  const pane = (window.location.hash || '').replace(/^#/, '');
  if (pane) activatePane(pane);
});

initDetail();
initBrowse();
