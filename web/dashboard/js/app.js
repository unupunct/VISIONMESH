/*
  Application shell: boots, decides between setup / login / dashboard, and routes.

  Routing is hash-based on purpose. It works when the dashboard is served from a subpath behind a
  reverse proxy, and it needs no server-side rewrite rules for anyone to get right.
*/

import { api, ApiError } from './api.js';
import { el, clear, mount, toast, loading, notice } from './ui.js';
import { store, on, emit, connectRealtime, disconnectRealtime, refreshCameras, refreshDevices, refreshSystem } from './store.js';

import { renderSetup } from './views/setup.js';
import { renderLogin } from './views/login.js';
import { renderCameras } from './views/cameras.js';
import { renderLiveView } from './views/live.js';
import { renderCameraDetail } from './views/camera-detail.js';
import { renderDevices } from './views/devices.js';
import { renderEvents } from './views/events.js';
import { renderRecordings } from './views/recordings.js';
import { renderStorage } from './views/storage.js';
import { renderNetwork } from './views/network.js';
import { renderSettings } from './views/settings.js';
import { renderHomeAssistant } from './views/homeassistant.js';
import { renderHelp } from './views/help.js';

const routes = {
  cameras: renderCameras,
  live: renderLiveView,
  camera: renderCameraDetail,
  devices: renderDevices,
  events: renderEvents,
  recordings: renderRecordings,
  storage: renderStorage,
  network: renderNetwork,
  settings: renderSettings,
  homeassistant: renderHomeAssistant,
  help: renderHelp,
};

/** Cleanup for the view currently on screen: stops its streams, timers and subscriptions. */
let disposeCurrentView = null;

async function boot() {
  const bootScreen = document.getElementById('boot');

  try {
    const status = await api.setupStatus();

    if (status.needsSetup) {
      bootScreen.hidden = true;
      renderSetup(status, () => location.reload());
      return;
    }

    try {
      store.user = await api.me();
    } catch (error) {
      if (error instanceof ApiError && error.isAuthError) {
        bootScreen.hidden = true;
        renderLogin(status, () => location.reload());
        return;
      }
      throw error;
    }

    await startDashboard();
    bootScreen.hidden = true;
  } catch (error) {
    bootScreen.hidden = true;
    document.body.appendChild(el('div', { class: 'auth-screen' },
      el('div', { class: 'auth-card card' },
        notice('error', 'VisionMesh could not start', error.message),
        el('button', { class: 'primary', onclick: () => location.reload() }, 'Try again'))));
  }
}

async function startDashboard() {
  document.getElementById('app').hidden = false;

  // Load everything the shell needs before showing a route, so no view has to guard against a
  // half-populated store on its first render.
  const [capabilities] = await Promise.all([
    api.capabilities(),
    refreshCameras(),
    refreshDevices(),
    refreshSystem(),
  ]);
  store.capabilities = capabilities;

  wireShell();
  connectRealtime();

  on('cameras', updateTopbarStats);
  on('system', updateTopbarStats);
  on('camera.state', updateTopbarStats);
  on('connection', updateConnectionIndicator);
  on('storage.warning', (message) => toast(message.message, 'error', 9000));

  updateTopbarStats();
  updateConnectionIndicator(false);

  window.addEventListener('hashchange', renderRoute);
  renderRoute();

  // The header counts come from the server's own view of the system rather than from whatever
  // the browser happens to have cached.
  setInterval(() => refreshSystem().catch(() => {}), 20000);
}

function wireShell() {
  const sidebar = document.getElementById('sidebar');
  const menuToggle = document.getElementById('menu-toggle');

  menuToggle.addEventListener('click', () => {
    const open = sidebar.classList.toggle('open');
    menuToggle.setAttribute('aria-expanded', String(open));
  });

  // On a phone the sidebar covers the content, so choosing a destination should close it.
  sidebar.addEventListener('click', (event) => {
    if (event.target.closest('a')) {
      sidebar.classList.remove('open');
      menuToggle.setAttribute('aria-expanded', 'false');
    }
  });

  const accountButton = document.getElementById('account-button');
  accountButton.textContent = (store.user?.username || '?').charAt(0).toUpperCase();
  accountButton.title = `${store.user?.username} (${store.user?.role})`;
  accountButton.addEventListener('click', showAccountMenu);

  const version = document.getElementById('server-version');
  if (version) version.textContent = `VisionMesh ${store.system?.version || ''}`;
}

function showAccountMenu() {
  import('./views/account.js').then((module) => module.showAccountMenu());
}

function updateTopbarStats() {
  const target = document.getElementById('topbar-stats');
  if (!target) return;

  const cameras = store.cameras;
  const online = cameras.filter((camera) => camera.state === 'Online').length;
  const offline = cameras.filter((camera) => camera.state === 'Offline').length;
  const storage = store.system?.storage;

  const usedPercent = storage && storage.totalBytes > 0
    ? Math.round((storage.totalBytes - storage.freeBytes) * 100 / storage.totalBytes)
    : null;

  mount(clear(target),
    el('span', {}, el('b', {}, String(cameras.length)), ' cameras'),
    el('span', {}, el('b', { style: { color: 'var(--live)' } }, String(online)), ' online'),
    offline > 0 ? el('span', {}, el('b', {}, String(offline)), ' offline') : null,
    usedPercent !== null ? el('span', {}, 'Storage ', el('b', {}, `${usedPercent}%`)) : null,
  );
}

function updateConnectionIndicator(connected) {
  const indicator = document.getElementById('connection-state');
  if (!indicator) return;

  indicator.classList.toggle('online', connected);
  indicator.classList.toggle('offline', !connected);
  indicator.title = connected
    ? 'Live updates are connected'
    : 'Live updates are disconnected. The dashboard is refreshing more slowly.';
}

function parseRoute() {
  const hash = location.hash.replace(/^#\/?/, '');
  const [name, ...rest] = hash.split('/');
  return { name: name || 'cameras', params: rest.map(decodeURIComponent) };
}

async function renderRoute() {
  const { name, params } = parseRoute();
  const content = document.getElementById('content');

  // Tearing the old view down first is what stops a departed camera page from leaving an MJPEG
  // connection open, which would keep the camera running for nobody.
  if (disposeCurrentView) {
    try { disposeCurrentView(); } catch (error) { console.error('View cleanup failed:', error); }
    disposeCurrentView = null;
  }

  for (const link of document.querySelectorAll('.sidebar a')) {
    link.classList.toggle('active', link.dataset.route === name);
  }

  const render = routes[name];
  if (!render) {
    clear(content).appendChild(notice('warn', 'Page not found', `There is no "${name}" page.`));
    return;
  }

  clear(content).appendChild(loading());
  content.focus();

  try {
    const result = await render(content, params);
    if (typeof result === 'function') disposeCurrentView = result;
  } catch (error) {
    if (error instanceof ApiError && error.isAuthError) {
      disconnectRealtime();
      location.reload();
      return;
    }
    clear(content).appendChild(notice('error', 'This page could not be loaded', error.message));
  }
}

/** Navigates, used by views instead of touching location directly. */
export function navigate(path) {
  location.hash = path.startsWith('#') ? path : `#/${path}`;
}

boot();
