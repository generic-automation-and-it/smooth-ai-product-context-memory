// Mímisbrunnr Console shell — nav + view wiring.
//
// The Console is credential-gated (HLD-009): no token, no console. On load the
// shell probes the presented token's capability (read vs read+write) and only
// then initialises the views. Browse is the default view (the first read view
// reachable through the loopback entry, P1). Status is initialised lazily on
// first activation so a browse-only session never issues the recall-feedback
// requests.
import { initBrowse, reopenFromUrl } from './browse.js';
import { initStatus } from './status.js';
import { initDetail } from './detail.js';
import { initGraph } from './graph.js';
import { initReview, refreshReview } from './review.js';
import { getToken, setToken, clearToken, isAuthed } from './auth.js';
import { getCapabilities } from './api.js';

let statusInitialized = false;
let graphInitialized = false;
let reviewInitialized = false;

let capability = null;

const nav = document.querySelector('#nav');
const panes = document.querySelectorAll('.pane');
const loginOverlay = document.querySelector('#login-overlay');
const loginForm = document.querySelector('#login-form');
const loginToken = document.querySelector('#login-token');
const loginError = document.querySelector('#login-error');
const capabilityBadge = document.querySelector('#capability-badge');
const signOut = document.querySelector('#sign-out');

function applyCapability() {
  const isWrite = capability === 'write';
  capabilityBadge.textContent = isWrite ? 'Read + write' : 'Read-only';
  capabilityBadge.hidden = false;
  signOut.hidden = false;
  document.body.dataset.capability = capability;
}

function showLogin() {
  loginOverlay.hidden = false;
  loginToken.focus();
}

async function probeCapability() {
  const result = await getCapabilities();
  if (result.timedOut || result.unreachable || !result.ok) {
    throw new Error('Could not verify the token. Check it and try again.');
  }
  const caps = Array.isArray(result.data?.capabilities) ? result.data.capabilities : [];
  capability = caps.includes('write') ? 'write' : 'read';
}

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

function boot() {
  initDetail();
  initBrowse();
  applyCapability();
  // Re-login can change the tier (read-only ↔ read+write). Review's
  // Approve/Reject buttons are rendered from the capability, so re-render the
  // pane if it was already initialised — its listeners stay wired once.
  if (reviewInitialized) refreshReview();
  loginOverlay.hidden = true;
}

async function handleLogin(event) {
  event.preventDefault();
  const value = loginToken.value.trim();
  if (!value) {
    loginError.textContent = 'Enter a token.';
    loginError.hidden = false;
    return;
  }
  setToken(value);
  loginError.hidden = true;
  loginToken.disabled = true;
  try {
    await probeCapability();
    boot();
  } catch {
    clearToken();
    loginToken.disabled = false;
    loginError.textContent = 'The token was not accepted by the API. Check it and try again.';
    loginError.hidden = false;
  }
}

function handleSignOut() {
  clearToken();
  capability = null;
  delete document.body.dataset.capability;
  capabilityBadge.hidden = true;
  signOut.hidden = true;
  loginToken.disabled = false;
  loginToken.value = '';
  showLogin();
}

loginForm.addEventListener('submit', handleLogin);
signOut.addEventListener('click', handleSignOut);

async function start() {
  if (!isAuthed()) {
    showLogin();
    return;
  }
  try {
    await probeCapability();
    boot();
  } catch {
    // Stale or rotated credential — require a fresh login.
    clearToken();
    showLogin();
  }
}

start();
