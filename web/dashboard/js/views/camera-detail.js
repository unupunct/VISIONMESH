/*
  The camera control panel.

  Shows one camera large, with only the controls that camera actually supports. PTZ appears only
  for cameras that report it; recording controls explain themselves when ffmpeg is missing rather
  than failing when pressed.
*/

import { api, streamUrl, snapshotUrl } from '../api.js';
import { store, on, refreshCameras } from '../store.js';
import {
  el, clear, notice, toast, loading, stateBadge, field, textInput, select,
  formatBitrate, formatDateTime, confirmDialog, openModal, explain, icon, mount } from '../ui.js';

export async function renderCameraDetail(content, [cameraId]) {
  let camera = await api.camera(cameraId);

  const image = el('img', {
    alt: `Live view of ${camera.name}`,
    style: { width: '100%', maxHeight: '70vh', objectFit: 'contain', background: '#05080c', borderRadius: 'var(--radius)' },
  });

  const statsRow = el('div', { class: 'stat-strip' });
  const controls = el('div', { class: 'toolbar' });
  const panels = el('div', { class: 'grid-2' });
  const badge = el('span');

  const head = el('div', { class: 'page-head' },
    el('div', {},
      el('div', { class: 'spread', style: { gap: '12px', justifyContent: 'flex-start' } },
        el('h1', { style: { margin: 0 } }, camera.name),
        badge),
      el('p', { class: 'subtitle' },
        [camera.groupName, camera.deviceName, humaniseSource(camera.sourceKind)].filter(Boolean).join(' · '))),
    el('button', { onclick: () => { location.hash = '#/cameras'; } }, '← All cameras'));

  mount(clear(content), head, el('div', { class: 'card', style: { padding: '10px' } }, image), statsRow, controls, panels);

  function startStream() {
    if (camera.privacyMode) {
      image.removeAttribute('src');
      image.alt = 'Privacy mode is on, so this camera is not capturing.';
      return;
    }
    image.src = streamUrl(camera.id);
  }

  function stopStream() {
    image.src = '';
  }

  function refreshHeader() {
    clear(badge).appendChild(stateBadge(camera.state));
    if (camera.health?.recording) {
      badge.appendChild(el('span', { class: 'badge recording', style: { marginLeft: '6px' } },
        el('span', { class: 'dot' }), 'Recording'));
    }
  }

  function refreshStats() {
    const health = camera.health || {};
    clear(statsRow).append(
      stat(health.width ? `${health.width}×${health.height}` : '—', 'Resolution'),
      stat(health.fps ? `${health.fps}` : '—', 'Frames per second'),
      stat(formatBitrate(health.bitrateBps), 'Bitrate'),
      stat(health.latencyMs !== null && health.latencyMs !== undefined ? `${Math.round(health.latencyMs)} ms` : '—', 'Latency'),
      stat(health.viewerCount ?? 0, 'Viewers'));
  }

  function refreshControls() {
    clear(controls);

    mount(controls, 
      el('button', {
        onclick: async () => {
          // The browser downloads the snapshot itself; the server never has to keep a copy.
          const link = el('a', { href: snapshotUrl(camera.id), download: `${camera.name}.jpg` });
          document.body.appendChild(link);
          link.click();
          link.remove();
        },
      }, icon('⧉'), 'Snapshot'),

      recordButton(),

      el('button', {
        onclick: async () => {
          const next = !camera.privacyMode;
          if (next && !await confirmDialog({
            title: 'Turn on privacy mode?',
            message: `${camera.name} will stop capturing and recording entirely until you turn privacy mode off. `
                   + 'Anyone viewing it will be told why.',
            confirmLabel: 'Turn on privacy mode',
          })) return;

          await api.setPrivacy(camera.id, next);
          await reload();
          toast(next ? 'Privacy mode is on. This camera is not capturing.' : 'Privacy mode is off.', 'success');
        },
      }, icon(camera.privacyMode ? '🔓' : '🔒'), camera.privacyMode ? 'End privacy mode' : 'Privacy mode'),

      el('button', {
        onclick: () => {
          if (image.requestFullscreen) image.requestFullscreen();
          else toast('This browser does not support full screen here.', 'info');
        },
      }, icon('⛶'), 'Full screen'),

      el('span', { class: 'spacer' }),

      el('button', { onclick: () => runTest(camera) }, 'Test connection'),
      el('button', { onclick: () => runDiagnosis(camera) }, 'Fix camera'),
      el('button', { onclick: () => openCameraSettings(camera, reload) }, icon('⚙'), 'Settings'));
  }

  function recordButton() {
    const capabilities = store.capabilities;

    if (!capabilities?.recording) {
      return el('button', {
        disabled: true,
        title: 'Recording needs ffmpeg, which is not installed on the server.',
      }, icon('⏺'), 'Record');
    }

    const recording = camera.health?.recording;
    return el('button', {
      class: recording ? 'danger' : '',
      onclick: async () => {
        try {
          await api.setRecording(camera.id, !recording);
          await reload();
          toast(recording ? 'Stopped recording.' : 'Recording started.', 'success');
        } catch (error) {
          toast(error.message, 'error');
        }
      },
    }, icon('⏺'), recording ? 'Stop recording' : 'Record');
  }

  function refreshPanels() {
    clear(panels);

    if (camera.ptzSupported) panels.appendChild(ptzPanel(camera));
    panels.appendChild(detailsPanel(camera));
  }

  async function reload() {
    camera = await api.camera(cameraId);
    refreshHeader();
    refreshStats();
    refreshControls();
    refreshPanels();
    startStream();
  }

  refreshHeader();
  refreshStats();
  refreshControls();
  refreshPanels();
  startStream();

  const unsubscribe = on('camera.health', (message) => {
    if (message.cameraId !== cameraId || !message.health) return;
    camera.health = message.health;
    camera.state = message.health.state;
    refreshHeader();
    refreshStats();
  });

  return () => {
    stopStream();
    unsubscribe();
  };
}

function stat(value, label) {
  return el('div', { class: 'stat' },
    el('div', { class: 'value' }, String(value)),
    el('div', { class: 'label' }, label));
}

function humaniseSource(sourceKind) {
  return {
    AgentCamera: 'USB camera',
    AndroidPhone: 'Android phone',
    IosPhone: 'iPhone or iPad',
    Rtsp: 'RTSP stream',
    Onvif: 'ONVIF camera',
  }[sourceKind] || sourceKind;
}

// ---- PTZ -------------------------------------------------------------------

function ptzPanel(camera) {
  // Continuous move keeps going until it is told to stop, so every control sends a stop on
  // release. Without that, a camera keeps panning after the user lets go.
  const move = async (pan, tilt, zoom) => {
    try { await api.ptz(camera.id, { pan, tilt, zoom }); }
    catch (error) { toast(error.message, 'error'); }
  };
  const stop = async () => {
    try { await api.ptz(camera.id, { stop: true }); }
    catch { /* the camera will stop on its own timeout */ }
  };

  const padButton = (label, pan, tilt) => el('button', {
    onmousedown: () => move(pan, tilt, 0),
    onmouseup: stop,
    onmouseleave: stop,
    ontouchstart: (event) => { event.preventDefault(); move(pan, tilt, 0); },
    ontouchend: (event) => { event.preventDefault(); stop(); },
    'aria-label': label,
  }, label);

  const zoomSlider = el('input', {
    type: 'range', min: '-1', max: '1', step: '0.1', value: '0',
    oninput: (event) => move(0, 0, Number(event.target.value)),
    onchange: (event) => { event.target.value = '0'; stop(); },
  });

  return el('div', { class: 'card' },
    el('h2', {}, 'Pan, tilt and zoom'),
    el('div', { class: 'ptz-pad', style: { margin: '14px 0' } },
      el('span'), padButton('▲', 0, 0.5), el('span'),
      padButton('◀', -0.5, 0), el('button', { class: 'center', onclick: stop, 'aria-label': 'Stop' }, '■'), padButton('▶', 0.5, 0),
      el('span'), padButton('▼', 0, -0.5), el('span')),
    el('label', {}, 'Zoom'),
    zoomSlider,
    el('p', { class: 'faint', style: { fontSize: '0.78rem' } },
      'Hold a direction to move. The camera stops when you let go.'));
}

// ---- details ---------------------------------------------------------------

function detailsPanel(camera) {
  const health = camera.health || {};
  const connection = camera.connection || {};

  const rows = [
    ['Source', humaniseSource(camera.sourceKind)],
    camera.deviceName ? ['On device', camera.deviceName] : null,
    ['Recording mode', camera.recordingMode === 'Off' ? 'Not recording' : camera.recordingMode],
    ['Keep recordings for', camera.retentionDays === 0 ? 'Forever' : `${camera.retentionDays} days`],
    ['Requested quality', `${camera.width} × ${camera.height} at ${camera.fps} fps`],
    ['Last frame', health.lastFrameUtc ? formatDateTime(health.lastFrameUtc) : 'never'],
    ['Frames received', (health.framesReceived ?? 0).toLocaleString()],
    connection.rtspUrl ? ['Stream address', connection.rtspUrl] : null,
    connection.manufacturer ? ['Camera', [connection.manufacturer, connection.model].filter(Boolean).join(' ')] : null,
    health.batteryPercent !== null && health.batteryPercent !== undefined
      ? ['Battery', `${health.batteryPercent}%${health.batteryCharging ? ' (charging)' : ''}`]
      : null,
  ].filter(Boolean);

  return el('div', { class: 'card' },
    el('h2', {}, 'Details'),
    health.lastError ? notice('warn', 'Last problem reported', health.lastError) : null,
    el('dl', { class: 'kv' },
      ...rows.flatMap(([key, value]) => [el('dt', {}, key), el('dd', { class: key === 'Stream address' ? 'mono' : '' }, String(value))])));
}

// ---- test & diagnose -------------------------------------------------------

async function runTest(camera) {
  const body = el('div', {}, loading('Testing the connection to this camera…'));
  const handle = openModal({ title: `Test connection — ${camera.name}`, body });

  try {
    const result = await api.testCamera(camera.id);

    mount(clear(handle.body), 
      result.ok
        ? notice('success', 'The camera is working', `Video arrived in ${Math.round(result.timeToFirstFrameMs ?? 0)} ms.`)
        : notice('error', 'No video from this camera', result.error || 'The camera did not send any video.'),
      el('dl', { class: 'kv' },
        el('dt', {}, 'Frames received'), el('dd', {}, String(result.framesReceived)),
        el('dt', {}, 'Frame rate'), el('dd', {}, result.measuredFps ? `${result.measuredFps} fps` : 'not enough frames to measure'),
        el('dt', {}, 'Bitrate'), el('dd', {}, formatBitrate(result.measuredBitrateBps)),
        el('dt', {}, 'Resolution'), el('dd', {}, result.resolution || '—'),
        el('dt', {}, 'Latency'), el('dd', {}, result.latencyMs ? `${Math.round(result.latencyMs)} ms` : '—')));
  } catch (error) {
    clear(handle.body).appendChild(notice('error', null, error.message));
  }
}

async function runDiagnosis(camera) {
  const body = el('div', {}, loading('Checking this camera end to end…'));
  const handle = openModal({ title: `Fix camera — ${camera.name}`, body, wide: true });

  try {
    const result = await api.diagnoseCamera(camera.id);

    const stepIcon = { ok: '✓', warning: '!', failed: '✕', skipped: '–' };
    const stepColour = {
      ok: 'var(--live)', warning: 'var(--warn)', failed: 'var(--danger)', skipped: 'var(--text-faint)',
    };

    mount(clear(handle.body), 
      result.healthy
        ? notice('success', 'No problems found', result.summary)
        : notice('error', 'Problem found', result.summary),
      result.recommendedAction ? notice('info', 'What to do', result.recommendedAction) : null,
      el('div', { style: { marginTop: '14px' } },
        ...result.steps.map((step) => el('div', {
          style: {
            display: 'flex', gap: '11px', padding: '9px 0', borderTop: '1px solid var(--border)',
          },
        },
          el('span', { style: { color: stepColour[step.status], fontWeight: '700', width: '16px' } }, stepIcon[step.status]),
          el('div', {},
            el('div', { style: { fontWeight: '500' } }, step.name),
            el('div', { class: 'dim', style: { fontSize: '0.85rem' } }, step.message),
            step.advice ? el('div', { class: 'faint', style: { fontSize: '0.82rem', marginTop: '3px' } }, step.advice) : null)))));
  } catch (error) {
    clear(handle.body).appendChild(notice('error', null, error.message));
  }
}

// ---- settings --------------------------------------------------------------

export function openCameraSettings(camera, onSaved) {
  const messages = el('div');
  const advanced = store.capabilities;

  const name = textInput({ value: camera.name });
  const group = textInput({ value: camera.groupName || '' });

  const recordingMode = select([
    { value: 'Off', label: 'Do not record', selected: camera.recordingMode === 'Off' },
    { value: 'Continuous', label: 'Record all the time', selected: camera.recordingMode === 'Continuous' },
    { value: 'Motion', label: 'Record when something moves', selected: camera.recordingMode === 'Motion' },
    { value: 'Scheduled', label: 'Record on a schedule', selected: camera.recordingMode === 'Scheduled' },
    { value: 'Manual', label: 'Only when I press record', selected: camera.recordingMode === 'Manual' },
  ]);

  const retention = textInput({ type: 'number', value: String(camera.retentionDays), min: '0', max: '3650' });
  const fps = textInput({ type: 'number', value: String(camera.fps), min: '1', max: '60' });
  const width = textInput({ type: 'number', value: String(camera.width), min: '160', max: '3840' });
  const height = textInput({ type: 'number', value: String(camera.height), min: '120', max: '2160' });
  const quality = el('input', { type: 'range', min: '20', max: '95', value: String(camera.quality) });

  const scheduleFields = el('div', { hidden: camera.recordingMode !== 'Scheduled' });
  const scheduleStart = el('input', { type: 'time', value: camera.connection?.scheduleStart || '22:00' });
  const scheduleEnd = el('input', { type: 'time', value: camera.connection?.scheduleEnd || '06:00' });
  mount(scheduleFields, 
    el('div', { class: 'field-row' },
      field('From', scheduleStart),
      field('Until', scheduleEnd)),
    el('p', { class: 'faint', style: { fontSize: '0.8rem' } },
      'If the end time is earlier than the start, the schedule runs overnight.'));

  const motionFields = el('div', { hidden: camera.recordingMode !== 'Motion' });
  const sensitivity = el('input', {
    type: 'range', min: '1', max: '100',
    value: String(camera.connection?.motionSensitivity ?? 50),
  });
  mount(motionFields, 
    field('Motion sensitivity', sensitivity,
      'Higher catches smaller movements but reacts more often to shadows, rain and insects.'),
    !advanced?.motionDetection
      ? notice('warn', 'Motion detection is unavailable', 'Motion recording needs ffmpeg, which is not installed on the server.')
      : null);

  recordingMode.addEventListener('change', () => {
    scheduleFields.hidden = recordingMode.value !== 'Scheduled';
    motionFields.hidden = recordingMode.value !== 'Motion';
  });

  const save = el('button', {
    class: 'primary',
    onclick: async () => {
      clear(messages);
      save.disabled = true;

      try {
        await api.updateCamera(camera.id, {
          name: name.value.trim(),
          groupName: group.value.trim(),
          recordingMode: recordingMode.value,
          retentionDays: Number(retention.value),
          fps: Number(fps.value),
          width: Number(width.value),
          height: Number(height.value),
          quality: Number(quality.value),
          scheduleStart: scheduleStart.value,
          scheduleEnd: scheduleEnd.value,
          motionSensitivity: Number(sensitivity.value),
        });

        toast('Camera settings saved.', 'success');
        handle.close();
        await refreshCameras();
        if (onSaved) onSaved();
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
        save.disabled = false;
      }
    },
  }, 'Save changes');

  const handle = openModal({
    title: `Settings — ${camera.name}`,
    wide: true,
    body: el('div', {},
      messages,
      field('Camera name', name),
      field('Group', group, 'Used to filter the camera wall.'),

      el('h3', { style: { marginTop: '18px' } }, 'Recording'),
      field('When to record', recordingMode),
      scheduleFields,
      motionFields,
      field('Keep recordings for (days)', retention, '0 keeps everything until the disk fills up.'),

      el('h3', { style: { marginTop: '18px' } }, 'Picture'),
      el('div', { class: 'field-row' },
        field('Width', width),
        field('Height', height),
        field('Frames per second', fps)),
      el('div', { class: 'field' },
        el('div', { class: 'spread' },
          el('label', {}, 'Picture quality'),
          explain('Picture quality',
            'Quality controls how much detail is kept when the picture is compressed.\n\n'
            + 'Higher quality means a clearer picture, more network use and more disk space.\n\n'
            + 'Lower quality means less network and less storage, with softer detail. '
            + 'For most cameras the middle of the range is a good place to be.')),
        quality),

      el('h3', { style: { marginTop: '18px' } }, 'Danger zone'),
      el('button', {
        class: 'danger',
        onclick: async () => {
          if (!await confirmDialog({
            title: `Remove ${camera.name}?`,
            message: 'The camera will be removed from VisionMesh. Recordings already on disk are kept.',
            confirmLabel: 'Remove camera',
            danger: true,
          })) return;

          await api.deleteCamera(camera.id);
          toast(`${camera.name} removed.`, 'success');
          handle.close();
          location.hash = '#/cameras';
        },
      }, 'Remove this camera')),
    footer: [
      el('button', { onclick: () => handle.close() }, 'Cancel'),
      save,
    ],
  });
}
