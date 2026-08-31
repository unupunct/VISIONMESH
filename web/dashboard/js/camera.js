/*
  The browser camera: a full VisionMesh agent implemented in the page.

  It does exactly what the Windows and Linux agents do — claim a pairing code for a device token,
  hold open the agent WebSocket, advertise its cameras, and push JPEG frames tagged with the
  binary frame header. The server has no idea it is talking to a browser, which is the point:
  there is one protocol and one server code path, not a special case for phones.

  The one thing it cannot do is run in the background. Phone operating systems suspend a
  background tab within seconds, and no amount of cleverness changes that. The page says so
  plainly and keeps the screen awake instead of pretending otherwise.
*/

import { el, clear, mount, notice, toast, field, textInput, select } from './ui.js';
import { buildFrame } from './frame.js';

const STORAGE_KEY = 'visionmesh.cameraDevice';
const PROTOCOL_VERSION = 1;

/** DeviceKind values as the server's enum orders them. */
const DEVICE_KIND = { WindowsAgent: 0, LinuxAgent: 1, AndroidApp: 2, IosApp: 3, ServerLocal: 4 };

const QUALITY_PROFILES = {
  high: { label: 'High quality — 1080p, 20 fps', width: 1920, height: 1080, fps: 20, quality: 80 },
  balanced: { label: 'Balanced — 720p, 15 fps', width: 1280, height: 720, fps: 15, quality: 72 },
  low: { label: 'Low power — 720p, 8 fps', width: 1280, height: 720, fps: 8, quality: 62 },
};

const state = {
  config: loadConfig(),
  socket: null,
  stream: null,
  facingMode: 'environment',
  profile: 'balanced',
  cameraName: '',
  slots: new Map(),          // slot -> { sequence, timer }
  streaming: false,
  wakeLock: null,
  reconnectDelay: 1000,
  reconnectTimer: null,
  stats: { fps: 0, sent: 0, bytes: 0, lastSample: Date.now(), framesSinceSample: 0, bytesSinceSample: 0 },
  battery: null,
};

const dom = {
  panel: document.getElementById('panel'),
  controls: document.getElementById('controls'),
  stats: document.getElementById('stats'),
  video: document.getElementById('video'),
  previewIdle: document.getElementById('preview-idle'),
  badge: document.getElementById('stream-badge'),
  linkState: document.getElementById('link-state'),
  linkLabel: document.getElementById('link-label'),
  footer: document.getElementById('footer'),
};

// ---- configuration ---------------------------------------------------------

function loadConfig() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function saveConfig(config) {
  state.config = config;
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(config)); } catch { /* private browsing */ }
}

function forgetConfig() {
  state.config = null;
  try { localStorage.removeItem(STORAGE_KEY); } catch { /* nothing to do */ }
}

// ---- boot ------------------------------------------------------------------

function boot() {
  // getUserMedia is gated on a secure context. On a plain-HTTP LAN address the browser will not
  // even prompt, so say why up front rather than letting the user hit a silent failure.
  if (!window.isSecureContext && !isLocalhost()) {
    clear(dom.panel).appendChild(notice('error', 'This page needs a secure connection',
      'Browsers only allow a page to use the camera over HTTPS, or from localhost. '
      + 'VisionMesh is being served over plain HTTP, so the camera cannot be opened here.\n\n'
      + 'Put VisionMesh behind HTTPS, or reach it over a private network such as Tailscale, which gives it a name '
      + 'browsers treat as secure.'));
    return;
  }

  if (!navigator.mediaDevices?.getUserMedia) {
    clear(dom.panel).appendChild(notice('error', 'This browser cannot use the camera',
      'Try Chrome on Android, or Safari on iPhone and iPad.'));
    return;
  }

  const code = readCodeFromUrl();
  if (code && !state.config) {
    showPairing(code);
    return;
  }

  if (!state.config) {
    showPairing('');
    return;
  }

  showCamera();
}

function isLocalhost() {
  return ['localhost', '127.0.0.1', '::1'].includes(location.hostname);
}

function readCodeFromUrl() {
  // The code arrives in the fragment, which never reaches the server in a request line and so
  // never lands in an access log on its way to being redeemed.
  const fragment = new URLSearchParams(location.hash.replace(/^#/, ''));
  return (fragment.get('code') || new URLSearchParams(location.search).get('code') || '').trim();
}

// ---- pairing ---------------------------------------------------------------

function showPairing(prefilledCode) {
  const codeInput = textInput({
    value: prefilledCode,
    placeholder: 'ABCD-EFGH',
    autocapitalize: 'characters',
    autocomplete: 'off',
    spellcheck: 'false',
    style: { textTransform: 'uppercase', fontSize: '1.15rem', letterSpacing: '0.1em', textAlign: 'center' },
  });

  const nameInput = textInput({ placeholder: 'Front door', value: suggestName() });
  const messages = el('div');

  const pairButton = el('button', {
    class: 'primary big-button',
    onclick: async () => {
      clear(messages);
      pairButton.disabled = true;
      pairButton.textContent = 'Pairing…';

      try {
        const response = await fetch('/api/pairing/claim', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            code: codeInput.value.trim().toUpperCase(),
            name: nameInput.value.trim() || suggestName(),
            kind: detectDeviceKindName(),
            platform: navigator.userAgent,
            // Served by the server, so its version is always the server's own. Reporting a number here
            // could only ever be redundant or, once it drifts, wrong.
            version: 'browser',
          }),
        });

        const body = await response.json();
        if (!response.ok) throw new Error(body.error || 'Pairing failed.');

        saveConfig({
          deviceId: body.deviceId,
          deviceToken: body.deviceToken,
          serverName: body.serverName,
          deviceName: nameInput.value.trim() || suggestName(),
        });

        // Drop the code from the address bar so a screenshot or a shared link cannot carry it.
        history.replaceState(null, '', location.pathname);

        toast('Paired. You can start the camera now.', 'success');
        showCamera();
      } catch (error) {
        messages.appendChild(notice('error', 'Could not pair', error.message));
        pairButton.disabled = false;
        pairButton.textContent = 'Pair with server';
      }
    },
  }, 'Pair with server');

  clear(dom.controls);
  clear(dom.stats);
  dom.stats.hidden = true;

  mount(clear(dom.panel),
    el('div', { class: 'card' },
      el('h2', {}, 'Use this device as a camera'),
      el('p', { class: 'dim', style: { fontSize: '0.88rem' } },
        'Enter the pairing code shown on your VisionMesh server, under Devices → Add device.'),
      messages,
      field('Pairing code', codeInput),
      field('Camera name', nameInput, 'How this camera appears in the dashboard.'),
      pairButton));

  clear(dom.footer).append('Nothing is sent anywhere until you press Start camera.');
}

function suggestName() {
  if (/android/i.test(navigator.userAgent)) return 'Android camera';
  if (/iphone/i.test(navigator.userAgent)) return 'iPhone camera';
  if (/ipad/i.test(navigator.userAgent)) return 'iPad camera';
  return 'Browser camera';
}

function detectDeviceKindName() {
  if (/android/i.test(navigator.userAgent)) return 'AndroidApp';
  if (/iphone|ipad|ipod/i.test(navigator.userAgent)) return 'IosApp';
  // A laptop browser is closest to a desktop agent, and the dashboard labels it accordingly.
  return navigator.platform?.startsWith('Win') ? 'WindowsAgent' : 'LinuxAgent';
}

function detectDeviceKindValue() {
  return DEVICE_KIND[detectDeviceKindName()] ?? DEVICE_KIND.AndroidApp;
}

// ---- camera screen ---------------------------------------------------------

function showCamera() {
  const facing = select([
    { value: 'environment', label: 'Rear camera', selected: state.facingMode === 'environment' },
    { value: 'user', label: 'Front camera', selected: state.facingMode === 'user' },
  ], { onchange: (event) => { state.facingMode = event.target.value; if (state.streaming) restartCapture(); } });

  const profile = select(
    Object.entries(QUALITY_PROFILES).map(([key, value]) => ({
      value: key, label: value.label, selected: key === state.profile,
    })),
    { onchange: (event) => { state.profile = event.target.value; if (state.streaming) restartCapture(); } });

  const startButton = el('button', {
    class: 'primary big-button',
    onclick: () => (state.streaming ? stopCamera() : startCamera()),
  }, 'Start camera');

  mount(clear(dom.panel),
    el('div', { class: 'card tight' },
      el('div', { class: 'spread' },
        el('div', {},
          el('strong', {}, state.config.deviceName || 'This device'),
          el('div', { class: 'faint', style: { fontSize: '0.78rem' } },
            `Paired with ${state.config.serverName || 'VisionMesh'}`)),
        el('button', {
          class: 'small',
          onclick: async () => {
            await stopCamera();
            forgetConfig();
            toast('This device is no longer paired.', 'info');
            showPairing('');
          },
        }, 'Unpair'))));

  mount(clear(dom.controls),
    el('div', { class: 'card' },
      field('Camera', facing),
      field('Quality', profile,
        'Lower quality uses less battery, less network and less space on the server.'),
      startButton));

  clear(dom.footer).append(
    el('div', {},
      'Keep this page open and the screen on. Phones stop a background tab within seconds, '
      + 'so VisionMesh cannot stream while this page is hidden.'));

  state.startButton = startButton;
  connect();
}

// ---- capture ---------------------------------------------------------------

async function startCamera() {
  const profile = QUALITY_PROFILES[state.profile];

  try {
    state.stream = await navigator.mediaDevices.getUserMedia({
      video: {
        facingMode: { ideal: state.facingMode },
        width: { ideal: profile.width },
        height: { ideal: profile.height },
        frameRate: { ideal: profile.fps },
      },
      audio: false,
    });
  } catch (error) {
    const message = error.name === 'NotAllowedError'
      ? 'Camera access was refused. Allow the camera for this site in your browser settings and try again.'
      : error.name === 'NotFoundError'
        ? 'No camera was found on this device.'
        : `The camera could not be started: ${error.message}`;

    clear(dom.panel).appendChild(notice('error', 'Camera not available', message));
    return;
  }

  dom.video.srcObject = state.stream;
  dom.video.hidden = false;
  dom.previewIdle.hidden = true;
  await dom.video.play().catch(() => {});

  state.streaming = true;
  state.startButton.textContent = 'Stop camera';
  state.startButton.classList.remove('primary');
  state.startButton.classList.add('danger');

  await requestWakeLock();
  updateBadge();

  // Tell the server what this device now offers, so it appears in Add Camera immediately.
  sendJson({ type: 'devices', devices: describeCameras() });
  dom.stats.hidden = false;
}

async function stopCamera() {
  state.streaming = false;

  for (const [, slot] of state.slots) {
    if (slot.timer) clearInterval(slot.timer);
  }
  state.slots.clear();

  if (state.stream) {
    // Stopping every track is what actually releases the camera and turns the indicator off.
    for (const track of state.stream.getTracks()) track.stop();
    state.stream = null;
  }

  dom.video.srcObject = null;
  dom.video.hidden = true;
  dom.previewIdle.hidden = false;
  dom.stats.hidden = true;

  releaseWakeLock();
  updateBadge();

  if (state.startButton) {
    state.startButton.textContent = 'Start camera';
    state.startButton.classList.add('primary');
    state.startButton.classList.remove('danger');
  }

  sendJson({ type: 'devices', devices: describeCameras() });
}

function restartCapture() {
  const wasStreaming = state.streaming;
  stopCamera().then(() => { if (wasStreaming) startCamera(); });
}

/**
 * The capture devices this browser advertises.
 *
 * A browser cannot enumerate cameras in any useful detail before permission is granted, so this
 * describes the two logical cameras every phone has rather than inventing hardware names.
 */
function describeCameras() {
  const profile = QUALITY_PROFILES[state.profile];
  const track = state.stream?.getVideoTracks()[0];
  const settings = track?.getSettings?.() || {};

  return [{
    sourceId: `browser:${state.facingMode}`,
    name: state.facingMode === 'user' ? 'Front camera' : 'Rear camera',
    description: 'Browser camera',
    available: true,
    formats: [{
      width: settings.width || profile.width,
      height: settings.height || profile.height,
      fps: settings.frameRate || profile.fps,
      format: 'MJPG',
      nativeJpeg: false,
    }],
  }];
}

// ---- protocol --------------------------------------------------------------

function connect() {
  if (!state.config) return;
  if (state.socket && (state.socket.readyState === WebSocket.OPEN || state.socket.readyState === WebSocket.CONNECTING)) return;

  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  // The agent endpoint takes the token as a query parameter because a browser WebSocket cannot
  // set an Authorization header. The token is the device's own, not a user session.
  const url = `${scheme}//${location.host}/agent/ws?token=${encodeURIComponent(state.config.deviceToken)}`;

  const socket = new WebSocket(url);
  socket.binaryType = 'arraybuffer';
  state.socket = socket;

  socket.onopen = () => {
    state.reconnectDelay = 1000;
    setLinkState(true);

    sendJson({
      type: 'hello',
      hello: {
        protocol: PROTOCOL_VERSION,
        deviceId: state.config.deviceId,
        name: state.config.deviceName,
        kind: detectDeviceKindValue(),
        platform: navigator.userAgent,
        // Served by the server, so its version is always the server's own. Reporting a number here
        // could only ever be redundant or, once it drifts, wrong.
        version: 'browser',
        devices: describeCameras(),
      },
    });
  };

  socket.onclose = (event) => {
    setLinkState(false);

    // 1008 is what the server sends when the token is not recognised. Retrying forever with a
    // revoked token is pointless, so the page asks to be paired again instead.
    if (event.code === 1008 || event.code === 1002) {
      clear(dom.panel).appendChild(notice('error', 'This device is no longer paired',
        'The server does not recognise this device any more. Pair it again to continue.'));
      forgetConfig();
      return;
    }

    scheduleReconnect();
  };

  socket.onerror = () => { /* onclose follows and handles it */ };

  socket.onmessage = (event) => {
    if (typeof event.data !== 'string') return;
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    handleServerMessage(message);
  };
}

function scheduleReconnect() {
  if (state.reconnectTimer || !state.config) return;

  state.reconnectTimer = setTimeout(() => {
    state.reconnectTimer = null;
    connect();
  }, state.reconnectDelay);

  state.reconnectDelay = Math.min(state.reconnectDelay * 2, 20000);
}

function handleServerMessage(message) {
  switch (message.type) {
    case 'welcome':
      break;

    case 'ping':
      sendJson({ type: 'pong' });
      break;

    case 'list-devices':
      sendJson({ type: 'devices', devices: describeCameras() });
      break;

    case 'start-capture':
      startSlot(message.start);
      break;

    case 'stop-capture':
      stopSlot(message.slot);
      break;
  }
}

function startSlot(command) {
  if (!command || state.slots.has(command.slot)) return;

  if (!state.streaming) {
    // The server wants video but the user has not started the camera. Saying so is better than
    // going quiet: the dashboard shows this as the camera's last error.
    sendJson({
      type: 'capture-error',
      slot: command.slot,
      cameraId: command.cameraId,
      message: 'The camera page is open but the camera has not been started. Press Start camera on the device.',
    });
    return;
  }

  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d', { alpha: false });
  const slot = { sequence: 0, timer: null, canvas, context, command };

  const interval = Math.max(40, Math.round(1000 / Math.max(1, command.fps || 15)));
  let busy = false;

  slot.timer = setInterval(async () => {
    // Skip a tick rather than queueing: on a slow phone, encoding can take longer than the
    // frame interval, and stacking those up would grow memory until the tab is killed.
    if (busy || !state.streaming || state.socket?.readyState !== WebSocket.OPEN) return;
    busy = true;

    try {
      await captureAndSend(slot);
    } catch {
      // A single dropped frame is not worth reporting; the next one usually works.
    } finally {
      busy = false;
    }
  }, interval);

  state.slots.set(command.slot, slot);
  sendJson({ type: 'capture-started', slot: command.slot, cameraId: command.cameraId });
  updateBadge();
}

function stopSlot(slotNumber) {
  const slot = state.slots.get(slotNumber);
  if (!slot) return;

  if (slot.timer) clearInterval(slot.timer);
  state.slots.delete(slotNumber);
  updateBadge();
}

async function captureAndSend(slot) {
  const video = dom.video;
  if (!video.videoWidth || !video.videoHeight) return;

  const target = QUALITY_PROFILES[state.profile];

  // Scale to fit the requested box without distorting, exactly as the server-side puller does.
  const scale = Math.min(1, target.width / video.videoWidth, target.height / video.videoHeight);
  const width = Math.max(2, Math.round(video.videoWidth * scale));
  const height = Math.max(2, Math.round(video.videoHeight * scale));

  if (slot.canvas.width !== width || slot.canvas.height !== height) {
    slot.canvas.width = width;
    slot.canvas.height = height;
  }

  slot.context.drawImage(video, 0, 0, width, height);

  const blob = await new Promise((resolve) =>
    slot.canvas.toBlob(resolve, 'image/jpeg', target.quality / 100));
  if (!blob) return;

  const jpeg = new Uint8Array(await blob.arrayBuffer());
  const payload = buildFrame(slot.command.slot, slot.sequence++, jpeg, width, height);

  if (state.socket?.readyState === WebSocket.OPEN) {
    state.socket.send(payload);
    recordSentFrame(jpeg.length);
  }
}

function sendJson(message) {
  if (state.socket?.readyState !== WebSocket.OPEN) return;
  state.socket.send(JSON.stringify(message));
}

// ---- telemetry & display ---------------------------------------------------

function recordSentFrame(byteLength) {
  const stats = state.stats;
  stats.sent++;
  stats.bytes += byteLength;
  stats.framesSinceSample++;
  stats.bytesSinceSample += byteLength;

  const now = Date.now();
  const elapsed = (now - stats.lastSample) / 1000;
  if (elapsed < 2) return;

  stats.fps = Math.round((stats.framesSinceSample / elapsed) * 10) / 10;
  stats.bitrate = Math.round((stats.bytesSinceSample * 8) / elapsed);
  stats.framesSinceSample = 0;
  stats.bytesSinceSample = 0;
  stats.lastSample = now;

  renderStats();
  sendTelemetry();
}

function renderStats() {
  const stats = state.stats;
  const track = state.stream?.getVideoTracks()[0];
  const settings = track?.getSettings?.() || {};

  const cell = (value, label) => el('div', { class: 'cell' },
    el('div', { class: 'v' }, value), el('div', { class: 'k' }, label));

  mount(clear(dom.stats),
    cell(settings.width ? `${settings.width}×${settings.height}` : '—', 'Size'),
    cell(stats.fps ? String(stats.fps) : '—', 'fps'),
    cell(stats.bitrate ? `${Math.round(stats.bitrate / 1000)}k` : '—', 'bit/s'),
    cell(stats.sent.toLocaleString(), 'Frames'),
    state.battery ? cell(`${Math.round(state.battery.level * 100)}%`, state.battery.charging ? 'Charging' : 'Battery') : null);
}

function sendTelemetry() {
  if (state.slots.size === 0) return;

  sendJson({
    type: 'telemetry',
    telemetry: {
      batteryPercent: state.battery ? Math.round(state.battery.level * 100) : null,
      batteryCharging: state.battery ? state.battery.charging : null,
      networkQuality: describeNetwork(),
      cameras: [...state.slots.values()].map((slot) => ({
        slot: slot.command.slot,
        fps: state.stats.fps || null,
        droppedFrames: null,
        width: slot.canvas.width,
        height: slot.canvas.height,
        error: null,
      })),
    },
  });
}

function describeNetwork() {
  const connection = navigator.connection;
  if (!connection) return null;

  // effectiveType is a coarse but honest summary; a made-up "signal strength" would not be.
  return connection.effectiveType ? connection.effectiveType.toUpperCase() : null;
}

function setLinkState(connected) {
  dom.linkState.classList.toggle('online', connected);
  dom.linkState.classList.toggle('offline', !connected);
  dom.linkLabel.textContent = connected ? 'Connected' : 'Reconnecting…';
}

function updateBadge() {
  clear(dom.badge);

  if (!state.streaming) return;

  dom.badge.appendChild(state.slots.size > 0
    ? el('span', { class: 'badge live' }, el('span', { class: 'dot' }), 'STREAMING')
    : el('span', { class: 'badge' }, el('span', { class: 'dot' }), 'READY'));
}

// ---- keeping the page alive ------------------------------------------------

async function requestWakeLock() {
  // A screen wake lock is the only supported way to keep a phone streaming. It is not a
  // background mode and does not pretend to be: the page still has to stay in the foreground.
  try {
    if ('wakeLock' in navigator) state.wakeLock = await navigator.wakeLock.request('screen');
  } catch {
    // Not supported, or refused because the page is not visible. Streaming still works while
    // the screen is on.
  }
}

function releaseWakeLock() {
  try { state.wakeLock?.release(); } catch { /* already gone */ }
  state.wakeLock = null;
}

document.addEventListener('visibilitychange', async () => {
  if (document.visibilityState === 'visible') {
    if (state.streaming) await requestWakeLock();
    connect();
  }
});

if (navigator.getBattery) {
  navigator.getBattery().then((battery) => {
    state.battery = battery;
    for (const event of ['levelchange', 'chargingchange']) {
      battery.addEventListener(event, () => { renderStats(); sendTelemetry(); });
    }
  }).catch(() => { /* not available, and that is fine */ });
}

window.addEventListener('pagehide', () => { stopCamera(); });

boot();
