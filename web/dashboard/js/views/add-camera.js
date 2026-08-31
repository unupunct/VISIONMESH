/*
  The Add Camera flow.

  Every path through this dialogue ends in a working camera without the user having to know what
  RTSP, ONVIF or a device path is. The technical fields exist, but they are the last resort rather
  than the starting point: VisionMesh looks for cameras first and only asks when it cannot find one.
*/

import { api } from '../api.js';
import { store, refreshDevices } from '../store.js';
import { el, clear, openModal, field, textInput, select, notice, toast, loading, icon, mount } from '../ui.js';

export function openAddCamera(onAdded) {
  const handle = openModal({
    title: 'Add camera',
    body: el('div'),
    footer: null,
    wide: true,
  });

  showSourcePicker(handle, onAdded);
}

function showSourcePicker(handle, onAdded) {
  const sources = [
    {
      glyph: '📷',
      title: 'USB or built-in camera',
      desc: 'A webcam plugged into this server, or into another computer running VisionMesh.',
      action: () => showAgentCameras(handle, onAdded),
    },
    {
      glyph: '📱',
      title: 'Android phone',
      desc: 'Use an Android phone or tablet as a camera.',
      action: () => showPhoneInstructions(handle, 'Android'),
    },
    {
      glyph: '🍎',
      title: 'iPhone or iPad',
      desc: 'Use an Apple device as a camera.',
      action: () => showPhoneInstructions(handle, 'iOS'),
    },
    {
      glyph: '🌐',
      title: 'IP camera',
      desc: 'Search the network for ONVIF cameras and add them automatically.',
      action: () => showOnvifDiscovery(handle, onAdded),
    },
    {
      glyph: '▶',
      title: 'RTSP stream',
      desc: 'Add a camera by typing its stream address.',
      action: () => showRtspForm(handle, onAdded),
    },
    {
      glyph: '🖥',
      title: 'Network computer',
      desc: 'Find other computers running the VisionMesh agent.',
      action: () => showAgentCameras(handle, onAdded),
    },
  ];

  const capabilities = store.capabilities;

  mount(clear(handle.body), 
    el('p', { class: 'dim' }, 'What kind of camera do you want to add?'),
    !capabilities?.ffmpeg?.available
      ? notice('warn', 'Network cameras are unavailable',
          'IP and RTSP cameras need ffmpeg, which is not installed on the server. USB cameras and phones work without it.')
      : null,
    el('div', { class: 'source-list' },
      ...sources.map((source) => {
        const needsFfmpeg = source.title.includes('IP camera') || source.title.includes('RTSP');
        const disabled = needsFfmpeg && !capabilities?.ffmpeg?.available;

        return el('button', {
          class: 'source-option',
          disabled,
          onclick: source.action,
        },
          icon(source.glyph),
          el('div', {},
            el('div', { class: 'title' }, source.title),
            el('div', { class: 'desc' }, disabled ? 'Needs ffmpeg on the server.' : source.desc)));
      })));
}

// ---- USB / agent cameras ---------------------------------------------------

async function showAgentCameras(handle, onAdded) {
  clear(handle.body).appendChild(loading('Looking for computers with cameras…'));

  try {
    await refreshDevices();
  } catch (error) {
    clear(handle.body).appendChild(notice('error', null, error.message));
    return;
  }

  const online = store.devices.filter((device) => device.connected);

  if (online.length === 0) {
    mount(clear(handle.body), 
      backButton(handle, onAdded),
      notice('info', 'No computers are connected yet',
        'Install the VisionMesh agent on a computer with a camera, then pair it with this server.'),
      el('h3', {}, 'How to add a computer'),
      el('ol', { class: 'dim' },
        el('li', {}, 'Download the VisionMesh agent for Windows or Linux from the releases page.'),
        el('li', {}, 'On this server, go to Devices and press Add device to get a pairing code.'),
        el('li', {}, 'On the other computer, run: ', el('code', { class: 'mono' }, 'VisionMesh.Agent pair')),
        el('li', {}, 'Enter this server address and the pairing code.'),
        el('li', {}, 'Come back here and the computer will be listed.')),
      el('div', { style: { marginTop: '14px' } },
        el('button', { class: 'primary', onclick: () => { location.hash = '#/devices'; handle.close(); } }, 'Go to Devices')));
    return;
  }

  const container = el('div');
  mount(clear(handle.body), backButton(handle, onAdded), container);

  for (const device of online) {
    const section = el('div', { class: 'card tight' });
    container.appendChild(section);

    mount(section, 
      el('div', { class: 'spread', style: { marginBottom: '10px' } },
        el('div', {},
          el('strong', {}, device.name),
          el('div', { class: 'faint', style: { fontSize: '0.78rem' } }, device.platform || device.kind)),
        el('button', {
          class: 'small',
          onclick: async () => {
            await api.refreshDevice(device.id);
            toast('Asked the computer to look again for cameras.', 'info');
            setTimeout(() => showAgentCameras(handle, onAdded), 1500);
          },
        }, 'Rescan')));

    let available;
    try {
      available = await api.deviceCameras(device.id);
    } catch (error) {
      section.appendChild(notice('warn', null, error.message));
      continue;
    }

    if (available.length === 0) {
      section.appendChild(el('p', { class: 'faint' },
        device.availableCameras?.length
          ? 'Every camera on this computer has already been added.'
          : 'No cameras were found on this computer.'));
      continue;
    }

    for (const capture of available) {
      section.appendChild(captureRow(device, capture, handle, onAdded));
    }
  }
}

function captureRow(device, capture, handle, onAdded) {
  const best = capture.formats?.[0];

  const details = [];
  if (best) details.push(`${best.width} × ${best.height} at ${Math.round(best.fps)} fps`);
  if (best?.nativeJpeg) details.push('sends JPEG directly, no re-encoding needed');

  return el('div', { class: 'spread', style: { padding: '9px 0', borderTop: '1px solid var(--border)' } },
    el('div', {},
      el('div', {}, capture.name),
      el('div', { class: 'faint', style: { fontSize: '0.78rem' } },
        capture.available ? details.join(' · ') : (capture.unavailable || 'Not available'))),
    capture.available
      ? el('button', {
          class: 'primary small',
          onclick: () => showNameStep(handle, onAdded, {
            name: capture.name,
            sourceKind: 'AgentCamera',
            deviceId: device.id,
            sourceId: capture.sourceId,
            formats: capture.formats || [],
          }),
        }, 'Use this camera')
      : el('span', { class: 'badge offline' }, 'Unavailable'));
}

// ---- ONVIF discovery -------------------------------------------------------

async function showOnvifDiscovery(handle, onAdded) {
  mount(clear(handle.body), 
    backButton(handle, onAdded),
    loading('Searching the network for cameras… this takes a few seconds.'));

  let found;
  try {
    found = await api.discoverOnvif(5);
  } catch (error) {
    mount(clear(handle.body), backButton(handle, onAdded), notice('error', null, error.message));
    return;
  }

  const list = el('div');
  mount(clear(handle.body), 
    backButton(handle, onAdded),
    el('div', { class: 'spread', style: { marginBottom: '12px' } },
      el('h3', { style: { margin: 0 } }, found.length === 1 ? '1 camera found' : `${found.length} cameras found`),
      el('button', { class: 'small', onclick: () => showOnvifDiscovery(handle, onAdded) }, 'Search again')),
    list);

  if (found.length === 0) {
    list.append(
      notice('info', 'No cameras answered',
        'VisionMesh asks cameras on the local network to identify themselves. Cameras on a different network, '
        + 'or with ONVIF switched off, will not answer.'),
      el('p', { class: 'dim' }, 'You can still add the camera by typing its stream address.'),
      el('button', { class: 'primary', onclick: () => showRtspForm(handle, onAdded) }, 'Add by address instead'));
    return;
  }

  for (const camera of found) {
    list.appendChild(el('div', { class: 'card tight spread' },
      el('div', {},
        el('strong', {}, camera.displayName),
        el('div', { class: 'faint', style: { fontSize: '0.78rem' } },
          [camera.hardware, camera.address].filter(Boolean).join(' · '))),
      el('button', {
        class: 'primary small',
        onclick: () => showOnvifCredentials(handle, onAdded, camera),
      }, 'Add')));
  }
}

function showOnvifCredentials(handle, onAdded, camera) {
  const messages = el('div');
  const username = textInput({ value: 'admin', autocomplete: 'off' });
  const password = el('input', { type: 'password', autocomplete: 'off' });

  const connect = el('button', {
    class: 'primary',
    onclick: async () => {
      clear(messages);
      connect.disabled = true;
      connect.textContent = 'Connecting…';

      try {
        const probe = await api.probeOnvif({
          address: camera.serviceAddress,
          username: username.value,
          password: password.value,
        });
        showOnvifProfiles(handle, onAdded, camera, probe, username.value, password.value);
      } catch (error) {
        messages.appendChild(notice('error',
          error.code === 'camera_auth' ? 'The camera did not accept those details' : 'Could not reach the camera',
          error.message));
        connect.disabled = false;
        connect.textContent = 'Connect';
      }
    },
  }, 'Connect');

  mount(clear(handle.body), 
    el('button', { class: 'ghost small', onclick: () => showOnvifDiscovery(handle, onAdded) }, '← Back to search results'),
    el('h3', { style: { marginTop: '12px' } }, camera.displayName),
    el('p', { class: 'dim' },
      'Most cameras need the username and password you set when you first configured them. '
      + 'If you have never changed them, check the label on the camera or its manual.'),
    messages,
    field('Username', username),
    field('Password', password),
    connect);
}

function showOnvifProfiles(handle, onAdded, camera, probe, username, password) {
  const usable = (probe.profiles || []).filter((profile) => profile.streamUri);

  if (usable.length === 0) {
    mount(clear(handle.body), 
      backButton(handle, onAdded),
      notice('error', 'This camera has no usable video stream',
        'The camera answered but did not offer a stream VisionMesh can read.'));
    return;
  }

  const list = el('div');

  mount(clear(handle.body), 
    el('button', { class: 'ghost small', onclick: () => showOnvifDiscovery(handle, onAdded) }, '← Back to search results'),
    el('h3', { style: { marginTop: '12px' } }, camera.displayName),
    probe.device
      ? el('p', { class: 'faint', style: { fontSize: '0.82rem' } },
          [probe.device.manufacturer, probe.device.model, probe.device.firmwareVersion].filter(Boolean).join(' · '))
      : null,
    el('p', { class: 'dim' },
      'This camera offers more than one video quality. The first is usually the best picture; '
      + 'a lower one uses less network and less disk.'),
    list);

  usable.forEach((profile, index) => {
    list.appendChild(el('div', { class: 'card tight spread' },
      el('div', {},
        el('strong', {}, profile.description || profile.name),
        el('div', { class: 'faint', style: { fontSize: '0.78rem' } },
          [
            index === 0 ? 'Best quality' : 'Lower quality',
            profile.ptzSupported ? 'supports pan/tilt/zoom' : null,
          ].filter(Boolean).join(' · '))),
      el('button', {
        class: index === 0 ? 'primary small' : 'small',
        onclick: () => showNameStep(handle, onAdded, {
          name: camera.displayName,
          sourceKind: 'Onvif',
          rtspUrl: profile.streamUri,
          onvifAddress: camera.serviceAddress,
          onvifProfileToken: profile.token,
          username,
          password,
          width: profile.width,
          height: profile.height,
          fps: profile.frameRate ? Math.round(profile.frameRate) : 15,
        }),
      }, 'Use this')));
  });
}

// ---- manual RTSP -----------------------------------------------------------

function showRtspForm(handle, onAdded) {
  const messages = el('div');
  const url = textInput({ placeholder: 'rtsp://192.168.1.50:554/stream1' });
  const username = textInput({ autocomplete: 'off' });
  const password = el('input', { type: 'password', autocomplete: 'off' });
  const transport = select([
    { value: 'Auto', label: 'Choose automatically', selected: true },
    { value: 'Tcp', label: 'TCP (more reliable)' },
    { value: 'Udp', label: 'UDP (lower latency)' },
  ]);

  mount(clear(handle.body), 
    backButton(handle, onAdded),
    el('p', { class: 'dim' },
      'Enter the camera’s stream address. It is usually printed in the camera’s manual or shown in its own app, '
      + 'and looks like rtsp://192.168.1.50:554/stream1'),
    messages,
    field('Stream address', url),
    field('Username', username, 'Leave blank if the camera does not need one.'),
    field('Password', password),
    field('Connection type', transport, 'Leave this on automatic unless the picture keeps breaking up.'),
    el('button', {
      class: 'primary',
      onclick: () => {
        clear(messages);
        if (!url.value.trim()) {
          messages.appendChild(notice('warn', null, 'Enter the camera’s stream address.'));
          return;
        }
        showNameStep(handle, onAdded, {
          name: '',
          sourceKind: 'Rtsp',
          rtspUrl: url.value.trim(),
          username: username.value,
          password: password.value,
          transport: transport.value,
        });
      },
    }, 'Continue'));
}

// ---- phone instructions ----------------------------------------------------

async function showPhoneInstructions(handle, platform) {
  clear(handle.body).appendChild(loading('Creating a pairing code…'));

  let pairing;
  try {
    pairing = await api.createPairingCode();
  } catch (error) {
    clear(handle.body).appendChild(notice('error', null, error.message));
    return;
  }

  const expires = new Date(pairing.expiresUtc);

  mount(clear(handle.body),
    backButton(handle, () => {}),
    el('h3', {}, `Use an ${platform} device as a camera`),
    el('p', { class: 'dim' },
      'There is nothing to install. Scan the code below with the device and it becomes a camera.'),
    el('ol', { class: 'dim', style: { lineHeight: '1.9' } },
      el('li', {}, `Make sure the ${platform} device is on the same Wi-Fi network as this server.`),
      el('li', {}, 'Point its camera at the code below and open the link it offers.'),
      el('li', {}, 'Choose the front or rear camera and give it a name, such as "Front door".'),
      el('li', {}, 'Press Start camera. It appears here automatically.')),

    el('div', { class: 'card', style: { textAlign: 'center' } },
      el('div', { id: 'vm-phone-qr' }),
      el('div', { class: 'faint', style: { fontSize: '0.76rem', textTransform: 'uppercase', letterSpacing: '0.08em', marginTop: '10px' } }, 'Or type this code'),
      el('div', { class: 'mono', style: { fontSize: '1.6rem', letterSpacing: '0.14em', margin: '6px 0' } }, pairing.code),
      el('div', { class: 'faint', style: { fontSize: '0.8rem' } },
        pairing.cameraUrl ? `at ${pairing.cameraUrl.split('#')[0]}` : ''),
      el('div', { class: 'faint', style: { fontSize: '0.8rem' } },
        `This code stops working at ${expires.toLocaleTimeString()}.`)),

    notice('info', 'The screen has to stay on',
      'A phone camera runs in the browser, and phone operating systems stop a page as soon as it is hidden. '
      + 'Keep the page open with the screen on, and keep the phone plugged in.'),

    notice('info', 'Why does the code expire?',
      'A pairing code is single use and short lived, so a code left on screen or photographed cannot be used later '
      + 'to add a device to your system.'));

  // Draw the QR after the container exists in the document.
  renderPairingQr(pairing.qrPayload, handle.body.querySelector('#vm-phone-qr'));
}

/** Draws the pairing QR into a container, falling back silently to the typed code. */
async function renderPairingQr(payload, container) {
  if (!container || !payload) return;

  try {
    const { encodeQr } = await import('../qr.js');
    const matrix = encodeQr(payload);
    const size = matrix.length;
    const scale = 5;
    const quiet = 4;
    const dimension = (size + quiet * 2) * scale;

    const parts = [];
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        if (matrix[y][x]) parts.push(`M${(x + quiet) * scale} ${(y + quiet) * scale}h${scale}v${scale}h-${scale}z`);
      }
    }

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', `0 0 ${dimension} ${dimension}`);
    svg.setAttribute('width', '210');
    svg.setAttribute('height', '210');
    svg.style.background = '#fff';
    svg.style.borderRadius = '8px';
    svg.style.display = 'block';
    svg.style.margin = '0 auto';

    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', parts.join(''));
    path.setAttribute('fill', '#000');
    svg.appendChild(path);

    container.appendChild(svg);
  } catch (error) {
    console.error('Could not draw the pairing QR code:', error);
  }
}

// ---- naming and creation ---------------------------------------------------

function showNameStep(handle, onAdded, draft) {
  const messages = el('div');
  const name = textInput({ value: draft.name || '', placeholder: 'Front door' });

  const groups = [...new Set(store.cameras.map((camera) => camera.groupName).filter(Boolean))];
  const group = el('input', {
    type: 'text',
    list: 'vm-groups',
    placeholder: 'Optional, e.g. Outside',
  });

  const datalist = el('datalist', { id: 'vm-groups' }, ...groups.map((value) => el('option', { value })));

  // Offer the resolutions the camera actually reports, not a generic list that may not apply.
  const formatOptions = (draft.formats || [])
    .slice(0, 12)
    .map((format, index) => ({
      value: `${format.width}x${format.height}`,
      label: `${format.width} × ${format.height}${format.nativeJpeg ? ' (no re-encoding)' : ''}`,
      selected: index === 0,
    }));

  const resolution = formatOptions.length > 0
    ? select(formatOptions)
    : select([
        { value: '640x480', label: '640 × 480 (lowest network use)' },
        { value: '1280x720', label: '1280 × 720 (recommended)', selected: true },
        { value: '1920x1080', label: '1920 × 1080 (best detail)' },
      ]);

  const fps = select([
    { value: '5', label: '5 fps (lowest network use)' },
    { value: '10', label: '10 fps' },
    { value: '15', label: '15 fps (recommended)', selected: true },
    { value: '25', label: '25 fps' },
    { value: '30', label: '30 fps (smoothest)' },
  ]);

  const add = el('button', {
    class: 'primary',
    onclick: async () => {
      clear(messages);
      add.disabled = true;
      add.textContent = 'Adding…';

      const [width, height] = resolution.value.split('x').map(Number);

      try {
        const created = await api.createCamera({
          name: name.value.trim() || draft.name,
          sourceKind: draft.sourceKind,
          deviceId: draft.deviceId,
          sourceId: draft.sourceId,
          groupName: group.value.trim() || null,
          rtspUrl: draft.rtspUrl,
          username: draft.username,
          password: draft.password,
          transport: draft.transport,
          onvifAddress: draft.onvifAddress,
          onvifProfileToken: draft.onvifProfileToken,
          width,
          height,
          fps: Number(fps.value),
        });

        toast(`${created.name} added.`, 'success');
        handle.close();
        if (onAdded) onAdded(created);
        location.hash = `#/camera/${encodeURIComponent(created.id)}`;
      } catch (error) {
        messages.appendChild(notice('error', 'The camera could not be added', error.message));
        add.disabled = false;
        add.textContent = 'Add camera';
      }
    },
  }, 'Add camera');

  mount(clear(handle.body), 
    backButton(handle, onAdded),
    el('p', { class: 'dim' }, 'Give the camera a name you will recognise in the app and in notifications.'),
    messages,
    field('Camera name', name),
    datalist,
    field('Group', group, 'Groups let you filter the camera wall, for example Inside and Outside.'),
    el('div', { class: 'field-row' },
      field('Picture size', resolution, 'Larger uses more network and more disk.'),
      field('Frame rate', fps, 'Higher is smoother but uses more of everything.')),
    add);

  name.focus();
  name.select();
}

function backButton(handle, onAdded) {
  return el('button', {
    class: 'ghost small',
    style: { marginBottom: '10px' },
    onclick: () => showSourcePicker(handle, onAdded),
  }, '← Choose a different source');
}
