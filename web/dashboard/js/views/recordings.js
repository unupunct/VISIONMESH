/*
  Recordings: a day timeline per camera, plus playback.

  The timeline is the useful part. A list of files answers "what exists"; a timeline answers
  "what was happening at half past two", which is the question people actually arrive with.
*/

import { api, recordingUrl, recordingDownloadUrl } from '../api.js';
import { store } from '../store.js';
import {
  el, clear, emptyState, notice, formatBytes, formatDuration, formatDateTime,
  formatTime, humanise, select, openModal, confirmDialog, toast, loading, mount } from '../ui.js';

export async function renderRecordings(content) {
  if (store.cameras.length === 0) {
    clear(content).appendChild(emptyState({
      glyph: '⏺',
      title: 'No cameras yet',
      message: 'Recordings appear here once you have a camera that is set to record.',
    }));
    return;
  }

  const state = {
    cameraId: store.cameras[0].id,
    date: new Date().toISOString().slice(0, 10),
  };

  const timelineHost = el('div');
  const listHost = el('div');

  const cameraPicker = select(
    store.cameras.map((camera, index) => ({ value: camera.id, label: camera.name, selected: index === 0 })),
    { onchange: (event) => { state.cameraId = event.target.value; load(); } });

  const datePicker = el('input', {
    type: 'date',
    value: state.date,
    max: new Date().toISOString().slice(0, 10),
    onchange: (event) => { state.date = event.target.value; load(); },
  });

  const shiftDay = (days) => {
    const date = new Date(`${state.date}T12:00:00`);
    date.setDate(date.getDate() + days);
    state.date = date.toISOString().slice(0, 10);
    datePicker.value = state.date;
    load();
  };

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Recordings'),
        el('p', { class: 'subtitle' }, 'Look back through what your cameras recorded.')),
      el('div', { class: 'toolbar', style: { margin: 0 } },
        cameraPicker,
        el('button', { class: 'small', onclick: () => shiftDay(-1), 'aria-label': 'Previous day' }, '‹'),
        datePicker,
        el('button', { class: 'small', onclick: () => shiftDay(1), 'aria-label': 'Next day' }, '›'))),
    !store.capabilities?.recording
      ? notice('warn', 'Recording is switched off',
          'Recording needs ffmpeg, which is not installed on the server. Nothing is being recorded until it is.')
      : null,
    timelineHost,
    listHost);

  async function load() {
    clear(timelineHost).appendChild(loading('Loading the timeline…'));
    clear(listHost);

    const from = new Date(`${state.date}T00:00:00`);
    const to = new Date(`${state.date}T23:59:59.999`);

    try {
      const data = await api.timeline(state.cameraId, from.toISOString(), to.toISOString());
      clear(timelineHost).appendChild(buildTimeline(data, from));
      clear(listHost).appendChild(buildList(data.segments));
    } catch (error) {
      clear(timelineHost).appendChild(notice('error', null, error.message));
    }
  }

  await load();
}

function buildTimeline(data, dayStart) {
  const dayMs = 24 * 60 * 60 * 1000;
  const timeline = el('div', { class: 'timeline' });

  const positionOf = (value) => {
    const offset = new Date(value).getTime() - dayStart.getTime();
    return Math.max(0, Math.min(100, (offset / dayMs) * 100));
  };

  // Hour gridlines, labelled every three hours so they stay readable on a phone.
  for (let hour = 0; hour <= 24; hour += 1) {
    const left = (hour / 24) * 100;
    timeline.appendChild(el('div', { class: 'tick', style: { left: `${left}%` } }));
    if (hour % 3 === 0 && hour < 24) {
      timeline.appendChild(el('div', { class: 'hour', style: { left: `${left}%` } }, `${String(hour).padStart(2, '0')}:00`));
    }
  }

  for (const segment of data.segments) {
    const left = positionOf(segment.startUtc);
    const right = positionOf(segment.endUtc);
    // A very short clip would otherwise be invisible, so every segment gets a minimum width.
    const width = Math.max(0.35, right - left);

    timeline.appendChild(el('div', {
      class: 'segment',
      style: { left: `${left}%`, width: `${width}%` },
      title: `${formatTime(segment.startUtc)} – ${formatTime(segment.endUtc)} · ${formatBytes(segment.sizeBytes)}`,
      onclick: (event) => { event.stopPropagation(); playRecording(segment); },
    }));
  }

  for (const event of data.events) {
    if (event.type === 'CameraOnline' || event.type === 'CameraOffline') continue;
    timeline.appendChild(el('div', {
      class: 'mark',
      style: { left: `${positionOf(event.timestampUtc)}%` },
      title: `${formatTime(event.timestampUtc)} · ${humanise(event.type)}${event.detail ? ` — ${event.detail}` : ''}`,
    }));
  }

  return el('div', { class: 'card' },
    el('div', { class: 'spread', style: { marginBottom: '10px' } },
      el('h2', { style: { margin: 0 } }, 'Timeline'),
      el('span', { class: 'faint', style: { fontSize: '0.8rem' } },
        `${data.segments.length} clip(s) · ${data.events.length} event(s)`)),
    timeline,
    el('p', { class: 'faint', style: { fontSize: '0.78rem', marginTop: '8px' } },
      'Blue blocks are recorded video. Orange marks are events such as motion. Click a block to play it.'));
}

function buildList(segments) {
  if (segments.length === 0) {
    return emptyState({
      glyph: '⏺',
      title: 'Nothing recorded on this day',
      message: 'Either the camera was not set to record, or nothing triggered a recording.',
    });
  }

  const rows = segments.map((segment) => el('tr', {},
    el('td', { class: 'nowrap' }, formatTime(segment.startUtc)),
    el('td', { class: 'nowrap faint' },
      formatDuration((new Date(segment.endUtc) - new Date(segment.startUtc)) / 1000)),
    el('td', { class: 'numeric faint' }, formatBytes(segment.sizeBytes)),
    el('td', { class: 'faint' }, humanise(segment.trigger)),
    el('td', { class: 'right nowrap' },
      el('button', { class: 'small', onclick: () => playRecording(segment) }, 'Play'),
      ' ',
      el('a', { class: 'button small', href: recordingDownloadUrl(segment.id) }, 'Download'),
      ' ',
      el('button', {
        class: 'small danger',
        onclick: async () => {
          if (!await confirmDialog({
            title: 'Delete this recording?',
            message: `The video file from ${formatDateTime(segment.startUtc)} will be permanently deleted.`,
            confirmLabel: 'Delete',
            danger: true,
          })) return;

          try {
            await api.deleteRecording(segment.id);
            toast('Recording deleted.', 'success');
            location.reload();
          } catch (error) {
            toast(error.message, 'error');
          }
        },
      }, 'Delete'))));

  return el('div', { class: 'card', style: { padding: 0 } },
    el('div', { class: 'table-wrap' },
      el('table', {},
        el('thead', {}, el('tr', {},
          el('th', {}, 'Started'),
          el('th', {}, 'Length'),
          el('th', { class: 'numeric' }, 'Size'),
          el('th', {}, 'Trigger'),
          el('th', {}))),
        el('tbody', {}, ...rows))));
}

function playRecording(segment) {
  const video = el('video', {
    controls: true,
    autoplay: true,
    playsinline: true,
    style: { width: '100%', borderRadius: 'var(--radius-sm)', background: '#000' },
    src: recordingUrl(segment.id),
  });

  const handle = openModal({
    title: `Recording — ${formatDateTime(segment.startUtc)}`,
    wide: true,
    body: el('div', {},
      video,
      el('p', { class: 'faint', style: { fontSize: '0.8rem', marginTop: '8px' } },
        `${formatBytes(segment.sizeBytes)} · ${humanise(segment.trigger)}`)),
    footer: [
      el('a', { class: 'button', href: recordingDownloadUrl(segment.id) }, 'Download'),
      el('button', { class: 'primary', onclick: () => handle.close() }, 'Close'),
    ],
    // Stopping playback on close matters: a hidden but still-playing video keeps streaming
    // the file from the server for no reason.
    onClose: () => { video.pause(); video.removeAttribute('src'); video.load(); },
  });
}
