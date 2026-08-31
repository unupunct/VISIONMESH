/*
  Devices: the computers and phones that supply cameras, plus the pairing flow that adds them.
*/

import { api } from '../api.js';
import { store, refreshDevices, on } from '../store.js';
import {
  el, clear, emptyState, notice, toast, formatRelative, openModal,
  confirmDialog, field, textInput, icon, mount } from '../ui.js';

export async function renderDevices(content) {
  const list = el('div');

  const head = el('div', { class: 'page-head' },
    el('div', {},
      el('h1', {}, 'Devices'),
      el('p', { class: 'subtitle' }, 'Computers and phones that provide cameras to this server.')),
    el('button', { class: 'primary', onclick: () => openPairing() }, icon('+'), 'Add device'));

  mount(clear(content), head, list);

  async function build() {
    await refreshDevices();
    clear(list);

    if (store.devices.length === 0) {
      list.appendChild(emptyState({
        glyph: '▤',
        title: 'No devices yet',
        message: 'Pair a computer or a phone with this server, and its cameras become available here.',
        action: el('button', { class: 'primary', onclick: () => openPairing() }, 'Add a device'),
      }));
      return;
    }

    for (const device of store.devices) {
      list.appendChild(deviceCard(device, build));
    }
  }

  await build();
  const unsubscribe = on('device.state', () => build());
  return () => unsubscribe();
}

function deviceCard(device, reload) {
  const cameras = device.availableCameras || [];

  return el('div', { class: 'card' },
    el('div', { class: 'spread' },
      el('div', {},
        el('div', { style: { display: 'flex', gap: '10px', alignItems: 'center' } },
          el('strong', {}, device.name),
          el('span', { class: `badge ${device.connected ? 'live' : 'offline'}` },
            el('span', { class: 'dot' }), device.connected ? 'Connected' : 'Offline')),
        el('div', { class: 'faint', style: { fontSize: '0.8rem', marginTop: '3px' } },
          [
            kindLabel(device.kind),
            device.platform,
            device.agentVersion ? `agent ${device.agentVersion}` : null,
            device.connected ? null : `last seen ${formatRelative(device.lastSeenUtc)}`,
            device.batteryPercent !== null && device.batteryPercent !== undefined
              ? `battery ${device.batteryPercent}%${device.batteryCharging ? ' (charging)' : ''}`
              : null,
          ].filter(Boolean).join(' · '))),
      el('div', { class: 'toolbar', style: { margin: 0 } },
        device.connected
          ? el('button', {
              class: 'small',
              onclick: async () => {
                await api.refreshDevice(device.id);
                toast('Asked the device to look again for cameras.', 'info');
                setTimeout(reload, 1500);
              },
            }, 'Rescan')
          : null,
        el('button', { class: 'small', onclick: () => renameDevice(device, reload) }, 'Rename'),
        el('button', {
          class: 'small danger',
          onclick: async () => {
            if (!await confirmDialog({
              title: `Remove ${device.name}?`,
              message: device.cameraCount > 0
                ? `This device supplies ${device.cameraCount} camera(s). Removing it removes those cameras too. `
                  + 'Recordings already on disk are kept.'
                : 'The device will have to be paired again before it can be used.',
              confirmLabel: 'Remove device',
              danger: true,
            })) return;

            const result = await api.deleteDevice(device.id);
            toast(result.camerasRemoved > 0
              ? `Device removed, along with ${result.camerasRemoved} camera(s).`
              : 'Device removed.', 'success');
            reload();
          },
        }, 'Remove'))),

    cameras.length > 0
      ? el('div', { style: { marginTop: '12px' } },
          el('h3', {}, `Cameras on this device (${cameras.length})`),
          ...cameras.map((camera) => el('div', {
            style: { display: 'flex', justifyContent: 'space-between', padding: '6px 0', fontSize: '0.86rem' },
          },
            el('span', {}, camera.name),
            el('span', { class: 'faint' },
              camera.available
                ? (camera.formats?.[0] ? `${camera.formats[0].width}×${camera.formats[0].height}` : 'available')
                : (camera.unavailable || 'unavailable')))))
      : device.connected
        ? el('p', { class: 'faint', style: { marginTop: '10px' } }, 'This device reported no cameras.')
        : null);
}

function kindLabel(kind) {
  return {
    WindowsAgent: 'Windows computer',
    LinuxAgent: 'Linux computer',
    AndroidApp: 'Android device',
    IosApp: 'Apple device',
    ServerLocal: 'This server',
  }[kind] || kind;
}

function renameDevice(device, reload) {
  const name = textInput({ value: device.name });

  const handle = openModal({
    title: 'Rename device',
    body: field('Device name', name, 'This is how the device appears in the dashboard.'),
    footer: [
      el('button', { onclick: () => handle.close() }, 'Cancel'),
      el('button', {
        class: 'primary',
        onclick: async () => {
          try {
            await api.renameDevice(device.id, name.value.trim());
            handle.close();
            toast('Device renamed.', 'success');
            reload();
          } catch (error) {
            toast(error.message, 'error');
          }
        },
      }, 'Save'),
    ],
  });
}

/** Shows a pairing code plus a scannable QR image drawn in the browser. */
export async function openPairing() {
  const body = el('div');
  const handle = openModal({ title: 'Add a device', body, wide: true });

  clear(body).appendChild(el('div', { class: 'loading' }, el('span', { class: 'spinner' }), 'Creating a pairing code…'));

  let pairing;
  try {
    pairing = await api.createPairingCode();
  } catch (error) {
    clear(body).appendChild(notice('error', null, error.message));
    return;
  }

  const expires = new Date(pairing.expiresUtc);
  const qr = await renderQr(pairing.qrPayload);

  clear(body).append(
    el('div', { class: 'grid-2' },
      el('div', {},
        el('h3', {}, 'Scan with a phone'),
        qr || notice('info', null, 'Type the code below into the app instead.'),
        el('div', { style: { textAlign: 'center', marginTop: '12px' } },
          el('div', { class: 'faint', style: { fontSize: '0.74rem', textTransform: 'uppercase', letterSpacing: '0.08em' } }, 'Pairing code'),
          el('div', { class: 'mono', style: { fontSize: '1.7rem', letterSpacing: '0.14em' } }, pairing.code),
          el('div', { class: 'faint', style: { fontSize: '0.8rem', marginTop: '6px' } },
            `Expires at ${expires.toLocaleTimeString()}`))),

      el('div', {},
        el('h3', {}, 'Adding a phone'),
        el('ol', { class: 'dim', style: { fontSize: '0.88rem', lineHeight: '1.8', paddingLeft: '18px' } },
          el('li', {}, 'Point the phone’s camera at the code and open the link it offers.'),
          el('li', {}, 'Give the camera a name and press Pair with server.'),
          el('li', {}, 'Press Start camera.')),
        el('p', { class: 'faint', style: { fontSize: '0.8rem' } },
          'There is nothing to install. The link opens a page that turns the phone into a camera.'),

        el('h3', { style: { marginTop: '16px' } }, 'Adding a computer'),
        el('ol', { class: 'dim', style: { fontSize: '0.88rem', lineHeight: '1.8', paddingLeft: '18px' } },
          el('li', {}, 'Install the VisionMesh agent on that computer.'),
          el('li', {}, 'Run: ', el('code', { class: 'mono' }, 'VisionMesh.Agent pair')),
          el('li', {}, 'Enter this server address: ', el('code', { class: 'mono' }, pairing.serverUrl || '')),
          el('li', {}, 'Enter the pairing code shown here.'),
          el('li', {}, 'Start the agent. It appears in this list straight away.')))),

    notice('info', 'This code is single use',
      'It works once and expires in a few minutes, so a code left on screen cannot be used later to join your system.'),

    pairing.cameraUrl
      ? el('p', { class: 'faint', style: { fontSize: '0.8rem' } },
          'Camera page: ', el('span', { class: 'mono' }, pairing.cameraUrl))
      : null);
}

/**
 * Draws a QR code as an SVG.
 *
 * Implemented here rather than pulled from a CDN: the dashboard must work on a network with no
 * internet access at all, which is exactly how a good surveillance install is set up.
 */
async function renderQr(text) {
  try {
    const { encodeQr } = await import('../qr.js');
    const matrix = encodeQr(text);
    const size = matrix.length;
    const scale = 6;
    const quiet = 4;
    const dimension = (size + quiet * 2) * scale;

    const parts = [];
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        if (matrix[y][x]) {
          parts.push(`M${(x + quiet) * scale} ${(y + quiet) * scale}h${scale}v${scale}h-${scale}z`);
        }
      }
    }

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', `0 0 ${dimension} ${dimension}`);
    svg.setAttribute('width', '220');
    svg.setAttribute('height', '220');
    svg.style.background = '#fff';
    svg.style.borderRadius = '8px';
    svg.style.display = 'block';
    svg.style.margin = '0 auto';

    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', parts.join(''));
    path.setAttribute('fill', '#000');
    svg.appendChild(path);

    return svg;
  } catch (error) {
    console.error('Could not draw the QR code:', error);
    return null;
  }
}
