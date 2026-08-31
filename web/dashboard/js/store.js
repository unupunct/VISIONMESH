/*
  Shared client state, kept in sync with the server over a WebSocket.

  The dashboard is push-driven rather than polling. A camera going offline should appear the
  instant it happens, and a room full of browsers polling a twenty-camera server every second is
  real load for no benefit. A slow polling fallback exists only for the case where the WebSocket
  cannot be established at all - some corporate proxies still block it.
*/

import { api } from './api.js';

const listeners = new Map();

export const store = {
  user: null,
  system: null,
  capabilities: null,
  cameras: [],
  devices: [],
  connected: false,
};

/** Subscribes to a named change. Returns an unsubscribe function. */
export function on(event, handler) {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event).add(handler);
  return () => listeners.get(event)?.delete(handler);
}

export function emit(event, payload) {
  const handlers = listeners.get(event);
  if (!handlers) return;

  for (const handler of handlers) {
    try {
      handler(payload);
    } catch (error) {
      // One misbehaving view must not stop the others from updating.
      console.error(`Error in "${event}" handler:`, error);
    }
  }
}

export async function refreshCameras() {
  store.cameras = await api.cameras();
  emit('cameras', store.cameras);
  return store.cameras;
}

export async function refreshDevices() {
  store.devices = await api.devices();
  emit('devices', store.devices);
  return store.devices;
}

export async function refreshSystem() {
  store.system = await api.system();
  emit('system', store.system);
  return store.system;
}

export function findCamera(id) {
  return store.cameras.find((camera) => camera.id === id) || null;
}

// ---- realtime --------------------------------------------------------------

let socket = null;
let reconnectDelay = 1000;
let reconnectTimer = null;
let pollTimer = null;

export function connectRealtime() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;

  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  socket = new WebSocket(`${protocol}//${location.host}/api/ws`);

  socket.onopen = () => {
    store.connected = true;
    reconnectDelay = 1000;
    stopPolling();
    emit('connection', true);
  };

  socket.onclose = () => {
    store.connected = false;
    emit('connection', false);
    scheduleReconnect();
  };

  socket.onerror = () => {
    // onclose always follows, and that is where reconnection is handled.
  };

  socket.onmessage = (event) => {
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    handleMessage(message);
  };
}

function scheduleReconnect() {
  if (reconnectTimer) return;

  // Back off to 15 seconds. Beyond that the user has bigger problems than a stale dashboard,
  // and hammering a down server helps nobody.
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectRealtime();
  }, reconnectDelay);

  reconnectDelay = Math.min(reconnectDelay * 2, 15000);

  // While the push channel is down, fall back to a slow poll so the dashboard is stale by
  // seconds rather than indefinitely.
  startPolling();
}

function startPolling() {
  if (pollTimer) return;
  pollTimer = setInterval(async () => {
    try {
      await refreshCameras();
      await refreshSystem();
    } catch {
      // The server is unreachable; the reconnect loop reports it.
    }
  }, 5000);
}

function stopPolling() {
  if (!pollTimer) return;
  clearInterval(pollTimer);
  pollTimer = null;
}

function handleMessage(message) {
  switch (message.type) {
    case 'camera.state': {
      const camera = findCamera(message.cameraId);
      if (camera) {
        camera.state = message.state;
        if (camera.health) camera.health.state = message.state;
      }
      emit('camera.state', message);
      break;
    }

    case 'camera.health': {
      const camera = findCamera(message.cameraId);
      if (camera && message.health) {
        camera.health = message.health;
        camera.state = message.health.state;
      }
      emit('camera.health', message);
      break;
    }

    case 'camera.recording': {
      const camera = findCamera(message.cameraId);
      if (camera && camera.health) camera.health.recording = message.recording;
      emit('camera.recording', message);
      break;
    }

    case 'camera.added':
    case 'camera.removed':
      // The message carries the entity, but a refetch keeps one authoritative shape in play
      // rather than two subtly different ones.
      refreshCameras().catch(() => {});
      emit(message.type, message);
      break;

    case 'device.state':
      refreshDevices().catch(() => {});
      emit('device.state', message);
      break;

    case 'event':
      emit('event', message.event);
      break;

    case 'storage.warning':
      emit('storage.warning', message);
      break;

    case 'system.changed':
      refreshSystem().catch(() => {});
      break;
  }
}

export function disconnectRealtime() {
  stopPolling();
  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
  if (socket) { socket.onclose = null; socket.close(); socket = null; }
  store.connected = false;
}
