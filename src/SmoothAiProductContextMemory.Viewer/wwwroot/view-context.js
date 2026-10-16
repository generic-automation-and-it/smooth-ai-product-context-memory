// Mímisbrunnr Console — the read context every pane shares.
//
// Two pieces of state do not belong to one pane:
//
//   * The kind scope (All | Memory | Understanding). It is a *scope*, not a vocabulary
//     filter: there is no distinct-kinds endpoint, so a list derived from the rows on
//     screen would collapse to the one kind it just narrowed to. The full vocabulary
//     (rule / nfr / decision / divergence / …) stays on each pane's own Kind control.
//   * The product the current Browse query carried. `/query` resolves a product to its
//     initiatives and repos and marks the rows it matched, so it is only known to the
//     request that asked for it — and a generated import command must carry the same
//     product the practitioner was looking at, not one guessed from the row.
//
// A pane's own kind control wins over the scope when both are set: one wire field
// (`kind`) takes one value, and the narrower, explicit choice is the pane's.
const KIND_ALL = '';
const MAX_PRODUCT_NAME_LENGTH = 200;

const subscribers = new Set();

let kindScope = KIND_ALL;
let queryProductName = '';

function notify(reason) {
  for (const subscriber of subscribers) subscriber(reason);
}

export function kindScopeValue() {
  return kindScope;
}

export function queryProductNameValue() {
  return queryProductName;
}

/** The kind to send: the pane's own filter when set, else the shared scope. Never both. */
export function resolveKind(paneKind) {
  const local = (paneKind || '').trim();
  return local || kindScope;
}

export function setQueryProductName(name) {
  const next = (name || '').trim().slice(0, MAX_PRODUCT_NAME_LENGTH);
  if (next === queryProductName) return;
  queryProductName = next;
  notify('product');
}

/** The kind currently drawn as active, so a pane can show the scope it inherited. */
export function initViewContext() {
  document.querySelectorAll('input[name="kind-scope"]').forEach((radio) => {
    radio.addEventListener('change', () => {
      kindScope = radio.value;
      notify('kind');
    });
  });
}

export function onViewContext(subscriber) {
  subscribers.add(subscriber);
  return () => subscribers.delete(subscriber);
}
