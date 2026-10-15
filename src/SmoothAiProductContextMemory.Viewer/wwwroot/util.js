// Mímisbrunnr Console — shared DOM/text helpers.
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

export function shortUuid(value) {
  return value ? String(value).slice(0, 8) : '';
}

export function fmtInstant(value) {
  if (!value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString(undefined, {
    dateStyle: 'short', timeStyle: 'short',
  });
}

export function plural(count, singular, pluralForm) {
  return count === 1 ? singular : pluralForm;
}

export function fmtValidity(memory) {
  const from = memory.validFrom ? fmtInstant(memory.validFrom) : '';
  if (!memory.validUntil) return from ? `${from} → open` : 'open';
  return `${from} → ${fmtInstant(memory.validUntil)}`;
}
