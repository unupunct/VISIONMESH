/*
  Small DOM and formatting helpers shared by every view.

  Everything here builds nodes rather than concatenating HTML strings. Camera names, device names
  and error messages all come from users or from cameras on the network, and building them into
  markup by hand is how an injection bug gets in.
*/

/** Creates an element. Children may be nodes, strings, or nested arrays; null and false are skipped. */
export function el(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);

  for (const [key, value] of Object.entries(attributes || {})) {
    if (value === null || value === undefined || value === false) continue;

    if (key === 'class') node.className = value;
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (key === 'style' && typeof value === 'object') Object.assign(node.style, value);
    else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'html') node.innerHTML = value;   // only ever used with literals in this codebase
    else if (value === true) node.setAttribute(key, '');
    else node.setAttribute(key, value);
  }

  append(node, children);
  return node;
}

function append(parent, children) {
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    if (Array.isArray(child)) append(parent, child);
    else if (child instanceof Node) parent.appendChild(child);
    else parent.appendChild(document.createTextNode(String(child)));
  }
}

/**
 * Appends children to a node, skipping null, undefined and false, and flattening arrays.
 *
 * The native ParentNode.append() stringifies null into a literal "null" text node, which is a
 * quiet and surprisingly ugly bug in any view that appends a conditional child.
 */
export function mount(parent, ...children) {
  append(parent, children);
  return parent;
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

export function icon(glyph, className = 'glyph') {
  return el('span', { class: className, 'aria-hidden': 'true' }, glyph);
}

// ---- formatting ------------------------------------------------------------

export function formatBytes(bytes) {
  if (bytes === null || bytes === undefined) return '—';
  if (bytes < 1024) return `${bytes} B`;

  const units = ['KB', 'MB', 'GB', 'TB', 'PB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }

  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

export function formatBitrate(bitsPerSecond) {
  if (!bitsPerSecond) return '—';
  if (bitsPerSecond < 1000) return `${Math.round(bitsPerSecond)} bit/s`;
  if (bitsPerSecond < 1_000_000) return `${(bitsPerSecond / 1000).toFixed(0)} kbit/s`;
  return `${(bitsPerSecond / 1_000_000).toFixed(1)} Mbit/s`;
}

export function formatDuration(seconds) {
  if (seconds === null || seconds === undefined) return '—';

  const total = Math.round(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const remainder = total % 60;

  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  if (minutes > 0) return `${minutes}m ${String(remainder).padStart(2, '0')}s`;
  return `${remainder}s`;
}

export function formatUptime(timespan) {
  // The API sends a .NET TimeSpan as "d.hh:mm:ss" or "hh:mm:ss".
  if (!timespan) return '—';

  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(timespan);
  if (!match) return timespan;

  const [, days, hours, minutes] = match;
  if (days) return `${days}d ${hours}h`;
  if (Number(hours) > 0) return `${Number(hours)}h ${Number(minutes)}m`;
  return `${Number(minutes)}m`;
}

export function formatTime(value) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatDateTime(value) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString([], {
    year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

export function formatRelative(value) {
  if (!value) return 'never';

  const date = new Date(value);
  const seconds = (Date.now() - date.getTime()) / 1000;

  if (seconds < 0) return 'just now';
  if (seconds < 45) return 'just now';
  if (seconds < 90) return 'a minute ago';
  if (seconds < 3600) return `${Math.round(seconds / 60)} minutes ago`;
  if (seconds < 7200) return 'an hour ago';
  if (seconds < 86400) return `${Math.round(seconds / 3600)} hours ago`;
  if (seconds < 172800) return 'yesterday';
  if (seconds < 2592000) return `${Math.round(seconds / 86400)} days ago`;
  return formatDateTime(value);
}

/** Turns an enum name like "CameraOffline" into "Camera offline" for display. */
export function humanise(value) {
  if (!value) return '';
  const spaced = String(value).replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

// ---- state badges ----------------------------------------------------------

const STATE_LABELS = {
  Online: 'Live',
  Offline: 'Offline',
  Degraded: 'Degraded',
  Paused: 'Paused',
  Privacy: 'Privacy',
};

export function stateBadge(state, extra = '') {
  const className = String(state || 'Offline').toLowerCase();
  return el('span', { class: `badge ${className === 'online' ? 'live' : className}` },
    el('span', { class: 'dot' }),
    STATE_LABELS[state] || humanise(state),
    extra ? ` ${extra}` : null);
}

// ---- toasts ----------------------------------------------------------------

export function toast(message, kind = 'info', timeoutMs = 4200) {
  const root = document.getElementById('toast-root');
  if (!root) return;

  const node = el('div', { class: `toast ${kind}`, role: 'status' }, message);
  root.appendChild(node);

  setTimeout(() => {
    node.style.transition = 'opacity 0.2s';
    node.style.opacity = '0';
    setTimeout(() => node.remove(), 220);
  }, timeoutMs);
}

// ---- modal -----------------------------------------------------------------

/**
 * Opens a modal. Returns a handle with close(); the caller owns the lifetime because most
 * dialogues here run a multi-step flow rather than a single confirm.
 */
export function openModal({ title, body, footer, wide = false, onClose }) {
  const root = document.getElementById('modal-root');
  const previouslyFocused = document.activeElement;

  const close = () => {
    document.removeEventListener('keydown', onKeyDown);
    backdrop.remove();
    if (previouslyFocused && previouslyFocused.focus) previouslyFocused.focus();
    if (onClose) onClose();
  };

  const onKeyDown = (event) => {
    if (event.key === 'Escape') { event.preventDefault(); close(); }
  };

  const modal = el('div', { class: `modal${wide ? ' wide' : ''}`, role: 'dialog', 'aria-modal': 'true' },
    el('div', { class: 'modal-head' },
      el('h2', {}, title),
      el('button', { class: 'icon-button', 'aria-label': 'Close', onclick: close }, '✕')),
    el('div', { class: 'modal-body' }, body),
    footer ? el('div', { class: 'modal-foot' }, footer) : null);

  const backdrop = el('div', {
    class: 'modal-backdrop',
    onclick: (event) => { if (event.target === backdrop) close(); },
  }, modal);

  root.appendChild(backdrop);
  document.addEventListener('keydown', onKeyDown);

  const firstField = modal.querySelector('input, select, textarea, button.primary');
  if (firstField) firstField.focus();

  return { close, modal, body: modal.querySelector('.modal-body'), footer: modal.querySelector('.modal-foot') };
}

/** Confirmation dialogue. Resolves true only if the user explicitly confirms. */
export function confirmDialog({ title, message, confirmLabel = 'Confirm', danger = false }) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (value) => { if (!settled) { settled = true; resolve(value); } };

    const handle = openModal({
      title,
      body: el('p', {}, message),
      footer: [
        el('button', { onclick: () => { finish(false); handle.close(); } }, 'Cancel'),
        el('button', {
          class: danger ? 'danger' : 'primary',
          onclick: () => { finish(true); handle.close(); },
        }, confirmLabel),
      ],
      onClose: () => finish(false),
    });
  });
}

// ---- building blocks -------------------------------------------------------

export function loading(message = 'Loading…') {
  return el('div', { class: 'loading' }, el('span', { class: 'spinner' }), message);
}

export function notice(kind, title, message) {
  return el('div', { class: `notice ${kind}` },
    title ? el('strong', {}, title) : null,
    message);
}

export function emptyState({ glyph = '▦', title, message, action }) {
  return el('div', { class: 'empty' },
    icon(glyph),
    el('h2', {}, title),
    message ? el('p', {}, message) : null,
    action || null);
}

export function field(label, control, hint) {
  const id = control.id || `f${Math.random().toString(36).slice(2, 9)}`;
  control.id = id;
  return el('div', { class: 'field' },
    el('label', { for: id }, label),
    control,
    hint ? el('div', { class: 'hint' }, hint) : null);
}

export function textInput(attributes = {}) {
  return el('input', { type: 'text', ...attributes });
}

export function select(options, attributes = {}) {
  const node = el('select', attributes);
  for (const option of options) {
    node.appendChild(el('option', { value: option.value, selected: option.selected }, option.label));
  }
  return node;
}

/**
 * A "What is this?" explainer. Advanced settings get one so a non-technical user is never
 * required to already know what a bitrate is in order to change one.
 */
export function explain(term, text) {
  return el('button', {
    class: 'ghost small',
    type: 'button',
    onclick: () => openModal({
      title: term,
      body: el('div', {}, ...text.split('\n\n').map((paragraph) => el('p', {}, paragraph))),
      footer: null,
    }),
  }, 'What is this?');
}
