/*
  Storage: what is on disk, how much room is left, and how long it will last.

  The projection is measured, not assumed. Every other tool in this space multiplies a guessed
  bitrate by a guessed number of hours; this one divides free space by what this server has
  actually been writing, and says nothing at all until it has enough history to be honest.
*/

import { api } from '../api.js';
import { el, clear, notice, formatBytes, loading, mount } from '../ui.js';

export async function renderStorage(content) {
  clear(content).appendChild(loading());

  const storage = await api.storage();
  const usedPercent = storage.usedPercent ?? 0;
  const meterClass = usedPercent > 92 ? 'bad' : usedPercent > 80 ? 'warn' : '';

  const perCamera = [...storage.perCamera].sort((a, b) => b.bytes - a.bytes);
  const largest = perCamera[0]?.bytes || 1;

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Storage'),
        el('p', { class: 'subtitle' }, 'Where recordings are kept and how much room is left.'))),

    storage.error ? notice('error', 'Storage problem', storage.error) : null,

    usedPercent > 92
      ? notice('warn', 'The disk is nearly full',
          'VisionMesh deletes the oldest recordings to make room, so you may be keeping less history than you expect. '
          + 'Reduce the retention period, set a storage limit, or add space.')
      : null,

    el('div', { class: 'stat-strip' },
      stat(formatBytes(storage.usedByRecordingsBytes), 'Used by recordings'),
      stat(formatBytes(storage.freeBytes), 'Free on this disk', usedPercent > 92 ? 'bad' : usedPercent > 80 ? 'warn' : 'good'),
      stat(formatBytes(storage.totalBytes), 'Disk size'),
      stat(storage.retentionDays === 0 ? 'Forever' : `${storage.retentionDays} days`, 'Keep recordings for')),

    el('div', { class: 'card' },
      el('h2', {}, 'This disk'),
      el('div', { class: `meter ${meterClass}` }, el('span', { style: { width: `${Math.min(100, usedPercent)}%` } })),
      el('div', { class: 'spread faint', style: { fontSize: '0.82rem' } },
        el('span', {}, `${usedPercent.toFixed(1)}% of the disk is in use`),
        el('span', { class: 'mono' }, storage.path)),

      storage.projectedDaysRemaining
        ? el('p', { style: { marginTop: '14px' } },
            'At the rate this server has actually been recording, the free space would last about ',
            el('strong', {}, `${storage.projectedDaysRemaining} more day(s)`),
            storage.retentionDays > 0
              ? ` — recordings are deleted after ${storage.retentionDays} days anyway, so this only matters if that is longer.`
              : '.')
        : el('p', { class: 'faint', style: { marginTop: '14px' } },
            'There is not enough recording history yet to work out how long the free space will last. '
            + 'This appears once VisionMesh has been recording for a while.'),

      storage.limitBytes > 0
        ? el('p', { class: 'faint' }, `A storage limit of ${formatBytes(storage.limitBytes)} is set. `
            + 'Once recordings reach it, the oldest are deleted even if they are inside the retention period.')
        : null),

    el('div', { class: 'card', style: { padding: 0 } },
      el('div', { style: { padding: '16px 16px 0' } }, el('h2', {}, 'Space used per camera')),
      perCamera.length === 0
        ? el('p', { class: 'faint', style: { padding: '0 16px 16px' } }, 'No recordings have been indexed yet.')
        : el('div', { class: 'table-wrap' },
            el('table', {},
              el('thead', {}, el('tr', {},
                el('th', {}, 'Camera'),
                el('th', {}, ''),
                el('th', { class: 'numeric' }, 'Used'),
                el('th', { class: 'numeric' }, 'Kept for'))),
              el('tbody', {}, ...perCamera.map((camera) => el('tr', {},
                el('td', {}, el('a', { href: `#/camera/${encodeURIComponent(camera.id)}` }, camera.name)),
                el('td', { style: { width: '40%' } },
                  el('div', { class: 'meter', style: { margin: 0 } },
                    el('span', { style: { width: `${(camera.bytes / largest) * 100}%` } }))),
                el('td', { class: 'numeric' }, formatBytes(camera.bytes)),
                el('td', { class: 'numeric faint' }, camera.retentionDays === 0 ? 'forever' : `${camera.retentionDays}d`)))))))
  );
}

function stat(value, label, tone) {
  return el('div', { class: 'stat' },
    el('div', { class: `value ${tone || ''}` }, String(value)),
    el('div', { class: 'label' }, label));
}
