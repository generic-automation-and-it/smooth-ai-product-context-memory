// Mímisbrunnr Console shell — nav + view wiring.
//
// Browse is the default view (the first read view reachable through the
// loopback entry, P1). Status is initialised lazily on first activation so a
// browse-only session never issues the recall-feedback requests. The SPA sends
// no Authorization header; Caddy injects the read token (LADR-02).
import { initBrowse } from './browse.js';
import { initStatus } from './status.js';
import { initDetail } from './detail.js';

let statusInitialized = false;

const nav = document.querySelector('#nav');
const panes = document.querySelectorAll('.pane');

nav.addEventListener('click', (event) => {
  const link = event.target.closest('.nav-link');
  if (!link) return;
  event.preventDefault();
  document.querySelectorAll('.nav-link').forEach((node) => {
    const active = node === link;
    node.classList.toggle('is-active', active);
    if (active) node.setAttribute('aria-current', 'page');
    else node.removeAttribute('aria-current');
  });
  panes.forEach((pane) => {
    pane.classList.toggle('is-active', pane.id === `pane-${link.dataset.pane}`);
  });
  if (link.dataset.pane === 'status' && !statusInitialized) {
    statusInitialized = true;
    initStatus();
  }
});

initDetail();
initBrowse();
