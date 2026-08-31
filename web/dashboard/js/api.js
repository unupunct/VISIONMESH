/*
  Thin client over the VisionMesh REST API.

  Authentication rides on the session cookie the server sets at login. That is what lets an
  <img src="/api/cameras/x/stream.mjpeg"> tag work without ever putting a token in a URL, which
  would otherwise end up in browser history, proxy logs and screenshots.
*/

/** Error carrying the server's own message, which is written for the person reading it. */
export class ApiError extends Error {
  constructor(message, status, code, body) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.body = body;
  }

  get isAuthError() { return this.status === 401; }
  get isForbidden() { return this.status === 403; }
}

async function request(method, path, body, options = {}) {
  const init = {
    method,
    headers: {},
    credentials: 'same-origin',
    signal: options.signal,
  };

  if (body !== undefined && body !== null) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(path, init);
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    // fetch only rejects on a genuine network failure, which for a self-hosted box almost always
    // means the server stopped or the network dropped - worth saying plainly.
    throw new ApiError('Cannot reach the VisionMesh server. Check that it is running.', 0, 'network');
  }

  if (response.status === 204) return null;

  const text = await response.text();
  let payload = null;
  if (text) {
    try { payload = JSON.parse(text); } catch { payload = text; }
  }

  if (!response.ok) {
    const message = (payload && payload.error) || `The server returned an error (${response.status}).`;
    throw new ApiError(message, response.status, payload && payload.code, payload);
  }

  return payload;
}

export const api = {
  get: (path, options) => request('GET', path, null, options),
  post: (path, body, options) => request('POST', path, body, options),
  put: (path, body, options) => request('PUT', path, body, options),
  patch: (path, body, options) => request('PATCH', path, body, options),
  delete: (path, options) => request('DELETE', path, null, options),

  // ---- auth ----
  login: (username, password) => request('POST', '/api/auth/login', { username, password }),
  logout: () => request('POST', '/api/auth/logout'),
  me: () => request('GET', '/api/auth/me'),

  // ---- setup ----
  setupStatus: () => request('GET', '/api/setup/status'),
  completeSetup: (payload) => request('POST', '/api/setup', payload),
  // Separate from the admin-only settings check: during setup there is no account to authorise one.
  testSetupPath: (recordingsPath) => request('POST', '/api/setup/test-path', { recordingsPath }),

  // ---- system ----
  system: () => request('GET', '/api/system'),
  capabilities: () => request('GET', '/api/system/capabilities'),
  network: () => request('GET', '/api/system/network'),
  settings: () => request('GET', '/api/settings'),
  saveSettings: (payload) => request('PUT', '/api/settings', payload),
  testStoragePath: (recordingsPath) => request('POST', '/api/settings/test-path', { recordingsPath }),

  // ---- cameras ----
  cameras: () => request('GET', '/api/cameras'),
  camera: (id) => request('GET', `/api/cameras/${encodeURIComponent(id)}`),
  cameraGroups: () => request('GET', '/api/cameras/groups'),
  createCamera: (payload) => request('POST', '/api/cameras', payload),
  updateCamera: (id, payload) => request('PATCH', `/api/cameras/${encodeURIComponent(id)}`, payload),
  deleteCamera: (id) => request('DELETE', `/api/cameras/${encodeURIComponent(id)}`),
  setPrivacy: (id, enabled) => request('POST', `/api/cameras/${encodeURIComponent(id)}/privacy?enabled=${enabled}`),
  setPaused: (id, paused) => request('POST', `/api/cameras/${encodeURIComponent(id)}/pause?paused=${paused}`),
  setRecording: (id, start) => request('POST', `/api/cameras/${encodeURIComponent(id)}/record?start=${start}`),
  ptz: (id, payload) => request('POST', `/api/cameras/${encodeURIComponent(id)}/ptz`, payload),
  testCamera: (id) => request('POST', `/api/cameras/${encodeURIComponent(id)}/test`),
  diagnoseCamera: (id) => request('POST', `/api/cameras/${encodeURIComponent(id)}/diagnose`),

  // ---- devices & pairing ----
  devices: () => request('GET', '/api/devices'),
  deviceCameras: (id) => request('GET', `/api/devices/${encodeURIComponent(id)}/cameras`),
  refreshDevice: (id) => request('POST', `/api/devices/${encodeURIComponent(id)}/refresh`),
  renameDevice: (id, name) => request('PATCH', `/api/devices/${encodeURIComponent(id)}`, { name }),
  deleteDevice: (id) => request('DELETE', `/api/devices/${encodeURIComponent(id)}`),
  createPairingCode: () => request('POST', '/api/pairing'),

  // ---- discovery ----
  discoverOnvif: (seconds = 4) => request('POST', `/api/discovery/onvif?seconds=${seconds}`),
  probeOnvif: (payload) => request('POST', '/api/discovery/onvif/probe', payload),

  // ---- archive ----
  events: (query = {}) => request('GET', `/api/events?${new URLSearchParams(clean(query))}`),
  eventTypes: () => request('GET', '/api/events/types'),
  recordings: (query = {}) => request('GET', `/api/recordings?${new URLSearchParams(clean(query))}`),
  timeline: (cameraId, from, to) =>
    request('GET', `/api/recordings/timeline?${new URLSearchParams({ cameraId, from, to })}`),
  deleteRecording: (id) => request('DELETE', `/api/recordings/${id}`),
  storage: () => request('GET', '/api/storage'),

  // ---- users ----
  users: () => request('GET', '/api/users'),
  createUser: (payload) => request('POST', '/api/users', payload),
  updateUser: (id, payload) => request('PATCH', `/api/users/${encodeURIComponent(id)}`, payload),
  deleteUser: (id) => request('DELETE', `/api/users/${encodeURIComponent(id)}`),
  changeOwnPassword: (currentPassword, newPassword) =>
    request('POST', '/api/account/password', { currentPassword, newPassword }),
  auditLog: (limit = 100) => request('GET', `/api/system/audit?limit=${limit}`),

  // ---- home assistant ----
  homeAssistant: () => request('GET', '/api/homeassistant'),
  saveHomeAssistant: (payload) => request('PUT', '/api/homeassistant', payload),
  testHomeAssistant: (payload) => request('POST', '/api/homeassistant/test', payload),
};

/** Drops empty values so they never appear as blank query parameters. */
function clean(query) {
  const result = {};
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') result[key] = value;
  }
  return result;
}

/** Live MJPEG URL for a camera. Authorised by the session cookie the browser already holds. */
export function streamUrl(cameraId) {
  // The cache-buster matters: without it a browser will happily reuse a dead stream response
  // when the same img element is pointed at the same URL again.
  return `/api/cameras/${encodeURIComponent(cameraId)}/stream.mjpeg?t=${Date.now()}`;
}

export function snapshotUrl(cameraId) {
  return `/api/cameras/${encodeURIComponent(cameraId)}/snapshot.jpg?t=${Date.now()}`;
}

export function recordingUrl(recordingId) {
  return `/api/recordings/${recordingId}/play`;
}

export function recordingDownloadUrl(recordingId) {
  return `/api/recordings/${recordingId}/download`;
}
