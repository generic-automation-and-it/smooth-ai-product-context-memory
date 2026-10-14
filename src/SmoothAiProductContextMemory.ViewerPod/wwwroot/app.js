// Mímisbrunnr Console shell — Phase A.
//
// Proves the loopback read path: the SPA sends NO Authorization header; Caddy
// injects the read token (LADR-02). A client-supplied header must not override,
// so this module never sets an Authorization header.

const nav = document.querySelector('#nav');
const conn = document.querySelector('#conn');
const proof = document.querySelector('#browse-proof');

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
  document.querySelectorAll('.pane').forEach((pane) => {
    pane.classList.toggle('is-active', pane.id === `pane-${link.dataset.pane}`);
  });
});

// Connectivity proof: a bounded query with no filters. This proves the loopback
// entry, not the browse view (P1). The browser never supplies a token.
async function proveReadPath() {
  conn.dataset.state = 'checking';
  conn.textContent = 'checking…';
  try {
    const response = await fetch('/api/context/query', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentOnly: true, limit: 5 }),
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();
    const count = Array.isArray(data?.items) ? data.items.length : '?';
    conn.dataset.state = 'ok';
    conn.textContent = 'connected';
    proof.dataset.state = 'ok';
    proof.textContent = `Read path OK — ${count} memory(ies) in the default query response.`;
  } catch (error) {
    conn.dataset.state = 'error';
    conn.textContent = 'unreachable';
    proof.dataset.state = 'error';
    proof.textContent = `Read path failed: ${error.message}. Is the AppHost stack running?`;
  }
}

proveReadPath();
