/*
  Live view: the camera wall with nothing else on it.

  Separate from the Cameras page because the two jobs are different. Cameras is for managing what
  you have; Live view is for watching, so it drops the captions and offers layouts meant for a
  screen left running on a wall.
*/

import { store, on, refreshCameras } from '../store.js';
import { el, clear, emptyState, stateBadge, mount } from '../ui.js';
import { createCameraTile } from './cameras.js';

const LAYOUTS = [
  { id: 'dense', label: 'Small' },
  { id: '', label: 'Medium' },
  { id: 'large', label: 'Large' },
];

const LAYOUT_KEY = 'visionmesh.liveLayout';

export async function renderLiveView(content) {
  await refreshCameras();

  const layout = { value: localStorage.getItem(LAYOUT_KEY) ?? '' };
  const grid = el('div', { class: `camera-grid ${layout.value}` });
  const tiles = new Map();
  const unsubscribers = [];

  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const tile = tiles.get(entry.target.dataset.cameraId);
      if (!tile) continue;
      if (entry.isIntersecting) tile.start(); else tile.stop();
    }
  }, { rootMargin: '200px' });

  const layoutChips = el('div', { class: 'chip-row' },
    ...LAYOUTS.map((option) => el('button', {
      class: `chip${layout.value === option.id ? ' active' : ''}`,
      onclick: () => {
        layout.value = option.id;
        localStorage.setItem(LAYOUT_KEY, option.id);
        grid.className = `camera-grid ${option.id}`;
        for (const chip of layoutChips.children) chip.classList.remove('active');
        for (const chip of layoutChips.children) {
          if (chip.textContent === option.label) chip.classList.add('active');
        }
      },
    }, option.label)));

  const fullscreenButton = el('button', {
    onclick: () => {
      // Full screen on the grid rather than the page keeps the wall usable on a dedicated display.
      if (document.fullscreenElement) document.exitFullscreen();
      else grid.requestFullscreen?.();
    },
  }, '⛶ Full screen');

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Live view'),
        el('p', { class: 'subtitle' }, 'Every camera, watched at once.')),
      el('div', { class: 'toolbar', style: { margin: 0 } }, layoutChips, fullscreenButton)),
    grid);

  function build() {
    for (const tile of tiles.values()) tile.stop();
    tiles.clear();
    observer.disconnect();
    clear(grid);

    const live = store.cameras.filter((camera) => camera.enabled);

    if (live.length === 0) {
      grid.appendChild(emptyState({
        glyph: '▶',
        title: 'Nothing to watch yet',
        message: 'Add a camera and it will appear here.',
        action: el('button', { class: 'primary', onclick: () => { location.hash = '#/cameras'; } }, 'Go to Cameras'),
      }));
      return;
    }

    for (const camera of live) {
      const tile = createCameraTile(camera, { showCaption: true });
      tiles.set(camera.id, tile);
      grid.appendChild(tile.node);
      observer.observe(tile.node);
    }
  }

  build();

  unsubscribers.push(on('camera.health', (message) => tiles.get(message.cameraId)?.update()));
  unsubscribers.push(on('camera.state', (message) => tiles.get(message.cameraId)?.update()));
  unsubscribers.push(on('camera.added', () => refreshCameras().then(build)));
  unsubscribers.push(on('camera.removed', () => refreshCameras().then(build)));

  return () => {
    observer.disconnect();
    for (const tile of tiles.values()) tile.stop();
    for (const unsubscribe of unsubscribers) unsubscribe();
  };
}
