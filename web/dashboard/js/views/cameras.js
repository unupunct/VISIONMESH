/*
  The main camera wall.

  This is the screen the product is judged on, so it does one thing well: show every camera, live,
  with its state legible at a glance and nothing else competing for attention.

  Tiles stream only while they are actually on screen. An IntersectionObserver starts and stops
  each stream as it scrolls in and out of view, which is what makes a twenty-camera wall viable on
  a phone: off-screen tiles cost nothing, and the server stops cameras nobody is watching.
*/

import { api, streamUrl } from '../api.js';
import { store, on, refreshCameras } from '../store.js';
import { el, clear, emptyState, stateBadge, formatBitrate, toast, icon, mount } from '../ui.js';
import { openAddCamera } from './add-camera.js';

export async function renderCameras(content) {
  await refreshCameras();

  const grid = el('div', { class: 'camera-grid' });
  const tiles = new Map();
  const unsubscribers = [];

  // Only stream what the viewer can actually see.
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const tile = tiles.get(entry.target.dataset.cameraId);
      if (!tile) continue;
      if (entry.isIntersecting) tile.start();
      else tile.stop();
    }
  }, { rootMargin: '150px' });

  const groupFilter = { value: null };

  const head = el('div', { class: 'page-head' },
    el('div', {},
      el('h1', {}, 'Cameras'),
      el('p', { class: 'subtitle' }, 'Everything VisionMesh is watching.')),
    el('div', { class: 'toolbar', style: { margin: 0 } },
      el('button', { class: 'primary', onclick: () => openAddCamera(() => rebuild()) },
        el('span', { 'aria-hidden': 'true' }, '+'), 'Add camera')));

  const groupBar = el('div', { class: 'chip-row', style: { marginBottom: '14px' } });

  mount(clear(content), head, groupBar, grid);

  function rebuild() {
    refreshCameras().then(() => {
      buildGroupBar();
      buildGrid();
    }).catch((error) => toast(error.message, 'error'));
  }

  function buildGroupBar() {
    const groups = [...new Set(store.cameras.map((camera) => camera.groupName).filter(Boolean))].sort();
    clear(groupBar);

    // A group filter that offers only "All" is noise, so it appears once groups exist.
    if (groups.length === 0) return;

    const makeChip = (label, value) => el('button', {
      class: `chip${groupFilter.value === value ? ' active' : ''}`,
      onclick: () => { groupFilter.value = value; buildGroupBar(); buildGrid(); },
    }, label);

    mount(groupBar, makeChip('All', null), ...groups.map((group) => makeChip(group, group)));
  }

  function buildGrid() {
    for (const tile of tiles.values()) tile.stop();
    tiles.clear();
    observer.disconnect();
    clear(grid);

    const visible = groupFilter.value
      ? store.cameras.filter((camera) => camera.groupName === groupFilter.value)
      : store.cameras;

    if (visible.length === 0) {
      grid.appendChild(store.cameras.length === 0
        ? emptyState({
            glyph: '▦',
            title: 'No cameras yet',
            message: 'Add a webcam from this computer, a phone, or a camera on your network. '
                   + 'VisionMesh will find most of them for you.',
            action: el('button', { class: 'primary', onclick: () => openAddCamera(() => rebuild()) }, 'Add your first camera'),
          })
        : emptyState({ glyph: '▦', title: 'Nothing in this group', message: 'No cameras are in this group yet.' }));
      return;
    }

    for (const camera of visible) {
      const tile = createCameraTile(camera);
      tiles.set(camera.id, tile);
      grid.appendChild(tile.node);
      observer.observe(tile.node);
    }
  }

  buildGroupBar();
  buildGrid();

  // Live updates: refresh the badge and stats in place rather than rebuilding the wall, which
  // would restart every stream on every state change.
  unsubscribers.push(on('camera.health', (message) => tiles.get(message.cameraId)?.update()));
  unsubscribers.push(on('camera.state', (message) => tiles.get(message.cameraId)?.update()));
  unsubscribers.push(on('camera.recording', (message) => tiles.get(message.cameraId)?.update()));
  unsubscribers.push(on('camera.added', () => rebuild()));
  unsubscribers.push(on('camera.removed', () => rebuild()));

  return () => {
    observer.disconnect();
    for (const tile of tiles.values()) tile.stop();
    for (const unsubscribe of unsubscribers) unsubscribe();
  };
}

/**
 * One camera tile. Owns its own <img> stream so it can be started and stopped independently.
 */
export function createCameraTile(camera, { showCaption = true, onOpen } = {}) {
  const view = el('div', { class: 'camera-view' });
  const overlay = el('div', { class: 'tile-overlay' });
  const badge = el('span');
  const recordingBadge = el('span');
  const meta = el('span', { class: 'meta' });
  const name = el('span', { class: 'name' }, camera.name);

  mount(overlay, badge, recordingBadge);

  const node = el('div', {
    class: 'camera-tile',
    role: 'button',
    tabindex: '0',
    dataset: { cameraId: camera.id },
    onclick: () => (onOpen ? onOpen(camera) : (location.hash = `#/camera/${encodeURIComponent(camera.id)}`)),
    onkeydown: (event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        if (onOpen) onOpen(camera); else location.hash = `#/camera/${encodeURIComponent(camera.id)}`;
      }
    },
  }, view, overlay, showCaption ? el('div', { class: 'camera-caption' }, name, meta) : null);

  let image = null;
  let started = false;

  function current() {
    return store.cameras.find((c) => c.id === camera.id) || camera;
  }

  function showPlaceholder(glyph, message) {
    clear(view).appendChild(el('div', { class: 'camera-placeholder' }, icon(glyph), message));
  }

  function start() {
    const latest = current();

    // Privacy mode is a promise that nothing is being captured. Opening a stream would break it,
    // and the tile must say so rather than showing a blank rectangle.
    if (latest.privacyMode) {
      stop();
      showPlaceholder('🔒', 'Privacy mode');
      update();
      return;
    }

    if (!latest.enabled) {
      stop();
      showPlaceholder('○', 'Switched off');
      update();
      return;
    }

    if (started) return;
    started = true;

    image = el('img', {
      alt: `Live view of ${latest.name}`,
      decoding: 'async',
      onerror: () => {
        // A stream can fail because the camera is genuinely down, which the badge already shows.
        if (started) showPlaceholder('⚠', 'No video');
      },
    });
    image.src = streamUrl(camera.id);
    clear(view).appendChild(image);
    update();
  }

  function stop() {
    started = false;
    if (image) {
      // Clearing src is what actually closes the HTTP connection; removing the element alone
      // can leave the request alive in some browsers.
      image.src = '';
      image.remove();
      image = null;
    }
  }

  function update() {
    const latest = current();
    name.textContent = latest.name;

    const health = latest.health;
    clear(badge);
    badge.appendChild(stateBadge(latest.state));

    clear(recordingBadge);
    if (health?.recording) {
      recordingBadge.appendChild(el('span', { class: 'badge recording' }, el('span', { class: 'dot' }), 'REC'));
    }

    const parts = [];
    if (health?.width) parts.push(`${health.width}×${health.height}`);
    if (health?.fps) parts.push(`${health.fps} fps`);
    if (health?.bitrateBps) parts.push(formatBitrate(health.bitrateBps));
    if (health?.batteryPercent !== null && health?.batteryPercent !== undefined) parts.push(`${health.batteryPercent}%`);
    meta.textContent = parts.join(' · ');
  }

  showPlaceholder('▦', 'Connecting…');
  update();

  return { node, start, stop, update };
}
