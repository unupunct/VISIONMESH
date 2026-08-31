/*
  Events: a filterable log of everything VisionMesh noticed.

  New events arrive over the realtime channel and are inserted at the top rather than triggering a
  refetch, so watching this page while something happens shows it happening.
*/

import { api } from '../api.js';
import { store, on } from '../store.js';
import { el, clear, emptyState, formatDateTime, humanise, select, notice, mount } from '../ui.js';

const SEVERITY_COLOUR = {
  Info: 'var(--text-dim)',
  Warning: 'var(--warn)',
  Error: 'var(--danger)',
};

export async function renderEvents(content) {
  const filters = { cameraId: '', type: '', limit: 100 };
  const tableBody = el('tbody');
  const summary = el('p', { class: 'subtitle' });

  const cameraFilter = select(
    [{ value: '', label: 'All cameras', selected: true },
     ...store.cameras.map((camera) => ({ value: camera.id, label: camera.name }))],
    { onchange: (event) => { filters.cameraId = event.target.value; load(); } });

  let types = [];
  try { types = await api.eventTypes(); } catch { types = []; }

  const typeFilter = select(
    [{ value: '', label: 'All event types', selected: true },
     ...types.map((type) => ({ value: type, label: humanise(type) }))],
    { onchange: (event) => { filters.type = event.target.value; load(); } });

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {}, el('h1', {}, 'Events'), summary),
      el('div', { class: 'toolbar', style: { margin: 0 } }, cameraFilter, typeFilter)),
    el('div', { class: 'card', style: { padding: '0' } },
      el('div', { class: 'table-wrap' },
        el('table', {},
          el('thead', {}, el('tr', {},
            el('th', { style: { width: '160px' } }, 'When'),
            el('th', { style: { width: '150px' } }, 'Event'),
            el('th', { style: { width: '170px' } }, 'Camera'),
            el('th', {}, 'Detail'))),
          tableBody))));

  async function load() {
    clear(tableBody).appendChild(el('tr', {}, el('td', { colspan: '4' }, el('div', { class: 'loading' }, 'Loading events…'))));

    try {
      const result = await api.events(filters);
      summary.textContent = result.total === 0
        ? 'Nothing has happened yet.'
        : `${result.total.toLocaleString()} event${result.total === 1 ? '' : 's'} recorded.`;

      clear(tableBody);

      if (result.items.length === 0) {
        tableBody.appendChild(el('tr', {}, el('td', { colspan: '4' },
          emptyState({ glyph: '≡', title: 'No events match', message: 'Try a different filter.' }))));
        return;
      }

      for (const event of result.items) tableBody.appendChild(eventRow(event));
    } catch (error) {
      clear(tableBody).appendChild(el('tr', {}, el('td', { colspan: '4' }, notice('error', null, error.message))));
    }
  }

  function eventRow(event) {
    const camera = store.cameras.find((c) => c.id === event.cameraId);

    return el('tr', {},
      el('td', { class: 'nowrap faint' }, formatDateTime(event.timestampUtc)),
      el('td', {},
        el('span', { style: { color: SEVERITY_COLOUR[event.severity] || 'inherit' } }, humanise(event.type))),
      el('td', {},
        camera
          ? el('a', { href: `#/camera/${encodeURIComponent(camera.id)}` }, camera.name)
          : el('span', { class: 'faint' }, event.cameraName || '—')),
      el('td', { class: 'dim' }, event.detail || ''));
  }

  await load();

  // Live insertion: honour the active filter so a filtered view stays filtered.
  const unsubscribe = on('event', (event) => {
    if (filters.cameraId && event.cameraId !== filters.cameraId) return;
    if (filters.type && event.type !== filters.type) return;

    const placeholder = tableBody.querySelector('td[colspan]');
    if (placeholder) clear(tableBody);

    tableBody.prepend(eventRow(event));
    while (tableBody.children.length > filters.limit) tableBody.lastChild.remove();
  });

  return () => unsubscribe();
}
