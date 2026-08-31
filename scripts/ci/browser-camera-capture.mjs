// Drives the browser camera in a real Chromium and checks that its frames come out of the server.
//
// The browser camera is a complete agent written in JavaScript: it pairs over REST, opens a
// WebSocket, speaks the same binary frame protocol as the compiled agents, and produces its
// frames with getUserMedia into a canvas. Pairing and the WebSocket handshake were already
// verified by hand. The capture half was not, because it needs a camera and a secure context.
//
// Chromium's fake capture device supplies both: --use-fake-device-for-media-stream gives a real
// MediaStream carrying a synthetic moving image, and 127.0.0.1 counts as a secure context, so
// every line of the capture path runs exactly as it would on a phone.
//
// Run from the repository root with a VisionMesh server already listening.

import { chromium } from 'playwright';

const PORT = Number(process.env.VM_PORT || 18151);
const BASE = `http://127.0.0.1:${PORT}`;
const PASSWORD = 'BrowserCheck!2026';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function fail(message) {
  console.error(`::error::${message}`);
  process.exit(1);
}

async function api(method, path, { token, body } = {}) {
  const response = await fetch(BASE + path, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await response.text();
  const parsed = text.trim() ? JSON.parse(text) : {};
  if (!response.ok) {
    throw new Error(`${method} ${path} -> ${response.status} ${JSON.stringify(parsed)}`);
  }
  return parsed;
}

/** Reads the MJPEG stream for a while and returns what arrived. */
async function readStream(cameraId, token, milliseconds) {
  const controller = new AbortController();
  const stop = setTimeout(() => controller.abort(), milliseconds);

  const chunks = [];
  try {
    const response = await fetch(`${BASE}/api/cameras/${cameraId}/stream.mjpeg`, {
      headers: { Authorization: `Bearer ${token}` },
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`stream responded ${response.status}`);

    for await (const chunk of response.body) chunks.push(Buffer.from(chunk));
  } catch (error) {
    // Aborting our own read is how this ends; anything else is real.
    if (error.name !== 'AbortError' && !/aborted/i.test(String(error))) throw error;
  } finally {
    clearTimeout(stop);
  }

  return Buffer.concat(chunks);
}

/** Pulls the real pixel dimensions out of a JPEG's start-of-frame marker. */
function readJpegSize(jpeg) {
  let i = 2;
  while (i < jpeg.length - 1) {
    if (jpeg[i] !== 0xff) { i += 1; continue; }
    const marker = jpeg[i + 1];
    if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) { i += 2; continue; }
    const length = jpeg.readUInt16BE(i + 2);
    if (marker === 0xc0 || marker === 0xc1 || marker === 0xc2) {
      return { height: jpeg.readUInt16BE(i + 5), width: jpeg.readUInt16BE(i + 7) };
    }
    i += 2 + length;
  }
  return null;
}

const main = async () => {
  // ---- prepare the server ----

  const status = await api('GET', '/api/setup/status');
  if (status.needsSetup) {
    await api('POST', '/api/setup', {
      body: {
        serverName: 'Browser CI',
        adminUsername: 'admin',
        adminPassword: PASSWORD,
        recordingsPath: '/tmp/vm-browser-data/recordings',
        retentionDays: 1,
      },
    });
  }

  const { token } = await api('POST', '/api/auth/login', {
    body: { username: 'admin', password: PASSWORD },
  });
  if (!token) fail('Could not sign in.');

  const pairing = await api('POST', '/api/pairing', { token, body: { label: 'Browser CI' } });
  console.log(`pairing code: ${pairing.code}`);

  // ---- drive a real browser ----

  const browser = await chromium.launch({
    args: [
      // A real MediaStream from a synthetic device: the page's getUserMedia, canvas draw and
      // toBlob all run for real, only the photons are fake.
      '--use-fake-device-for-media-stream',
      '--use-fake-ui-for-media-stream',
    ],
  });

  const context = await browser.newContext({ permissions: ['camera'] });
  const page = await context.newPage();

  page.on('console', (message) => console.log(`  [page ${message.type()}] ${message.text()}`));
  page.on('pageerror', (error) => console.log(`  [page error] ${error.message}`));

  await page.goto(`${BASE}/camera.html#code=${pairing.code}`);

  const secure = await page.evaluate(() => ({
    secureContext: window.isSecureContext,
    hasGetUserMedia: Boolean(navigator.mediaDevices?.getUserMedia),
  }));
  console.log('page context:', secure);
  if (!secure.secureContext) fail('The page is not a secure context, so getUserMedia cannot run.');

  await page.getByRole('button', { name: 'Pair with server' }).click();

  const startButton = page.getByRole('button', { name: 'Start camera' });
  await startButton.waitFor({ state: 'visible', timeout: 30_000 });
  console.log('paired');

  await startButton.click();

  // The button becoming "Stop camera" is the page's own signal that getUserMedia resolved and
  // frames are being produced.
  await page.getByRole('button', { name: 'Stop camera' }).waitFor({ state: 'visible', timeout: 30_000 });
  console.log('camera started in the browser');

  const video = await page.evaluate(() => {
    const element = document.getElementById('video');
    return { width: element?.videoWidth ?? 0, height: element?.videoHeight ?? 0 };
  });
  console.log('browser video element:', video);
  if (!video.width || !video.height) fail('The page has no video dimensions, so nothing is being captured.');

  // ---- does any of it reach the server? ----

  let device = null;
  for (let attempt = 0; attempt < 45 && !device; attempt++) {
    const devices = await api('GET', '/api/devices', { token });
    device = devices.find((d) => d.connected) ?? null;
    if (!device) await sleep(1000);
  }
  if (!device) fail('The browser never appeared as a connected device.');
  console.log(`device connected: ${device.id} (agent ${device.agentVersion}, ${device.kind})`);

  const sources = await api('GET', `/api/devices/${device.id}/cameras`, { token });
  if (!sources.length) fail('The browser advertised no cameras.');
  console.log(`advertised: ${sources.map((s) => `${s.name} (${s.sourceId})`).join(', ')}`);

  const camera = await api('POST', '/api/cameras', {
    token,
    body: {
      name: 'Browser camera',
      sourceKind: 'AgentCamera',
      deviceId: device.id,
      sourceId: sources[0].sourceId,
      width: 640,
      height: 480,
      fps: 10,
      quality: 70,
    },
  });
  console.log(`camera added: ${camera.id}`);

  const stream = await readStream(camera.id, token, 20_000);
  console.log(`captured ${stream.length} bytes from the stream endpoint`);

  if (stream.length < 20_000) {
    fail(`Only ${stream.length} bytes arrived, so the browser's frames are not reaching the server.`);
  }

  // Bytes are not frames.
  let frames = 0;
  for (let i = 0; i + 2 < stream.length; i++) {
    if (stream[i] === 0xff && stream[i + 1] === 0xd8 && stream[i + 2] === 0xff) frames++;
  }
  console.log(`JPEG start-of-image markers: ${frames}`);
  if (frames < 5) fail(`Only ${frames} JPEG frames in ${stream.length} bytes.`);

  const start = stream.indexOf(Buffer.from([0xff, 0xd8, 0xff]));
  const end = stream.indexOf(Buffer.from([0xff, 0xd9]), start);
  if (end === -1) fail('No complete JPEG arrived.');

  const jpeg = stream.subarray(start, end + 2);
  const size = readJpegSize(jpeg);
  if (!size) fail('The frame has no start-of-frame marker, so it is not a decodable JPEG.');

  console.log(`first frame: ${size.width}x${size.height}, ${jpeg.length} bytes`);
  if (size.width < 160 || size.height < 120) {
    fail(`A ${size.width}x${size.height} frame is too small to be real video.`);
  }

  // Two invariants that hold whoever ends up owning the resolution.
  //
  // The browser camera scales to the profile chosen on the phone itself, not to the size the
  // server asked for: the phone pays the battery and bandwidth cost, so it keeps that choice. The
  // width and height in the server's start-capture command are currently ignored, which is worth
  // knowing when reading the dashboard's per-camera size fields for one of these.
  const profile = { width: 1280, height: 720 };   // the page's default "Balanced" profile

  if (size.width > video.width || size.height > video.height) {
    fail(`Frame is ${size.width}x${size.height}, larger than the ${video.width}x${video.height} `
      + 'the camera produced. Upscaling before sending would waste bandwidth for no detail.');
  }

  if (size.width > profile.width || size.height > profile.height) {
    fail(`Frame is ${size.width}x${size.height}, outside the ${profile.width}x${profile.height} `
      + 'profile box the page selected.');
  }

  // Fitting a box means one dimension should actually reach it, otherwise the scaling maths has
  // quietly shrunk everything.
  const fitted = size.width === Math.min(video.width, profile.width)
    || size.height === Math.min(video.height, profile.height);
  if (!fitted) {
    fail(`Frame is ${size.width}x${size.height}, which touches neither the source size nor the `
      + 'profile box, so the scaling is wrong.');
  }

  await browser.close();
  console.log('The browser camera captured real frames and the server served them.');
};

main().catch((error) => fail(error.stack || String(error)));
