/*
  Network: how this server is reachable, and on which interfaces.

  Most remote-access problems are one of two things: the phone is on a different network, or the
  user is trying an address that belongs to a VPN or Docker interface. Showing every address with
  its interface, and marking which one a phone on the same Wi-Fi should use, answers both.
*/

import { api } from '../api.js';
import { el, clear, notice, loading, toast, mount } from '../ui.js';

export async function renderNetwork(content) {
  clear(content).appendChild(loading());

  const [network, system] = await Promise.all([api.network(), api.system()]);

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Network'),
        el('p', { class: 'subtitle' }, 'How devices and browsers reach this server.'))),

    el('div', { class: 'card' },
      el('h2', {}, 'Dashboard addresses'),
      el('p', { class: 'dim' },
        'Open any of these from another device on the same network. The first is usually the right one.'),
      ...network.dashboardUrls.map((url, index) => el('div', {
        class: 'spread',
        style: { padding: '8px 0', borderTop: index === 0 ? 'none' : '1px solid var(--border)' },
      },
        el('span', { class: 'mono' }, url),
        el('div', {},
          index === 0 ? el('span', { class: 'badge live' }, 'Recommended') : null,
          ' ',
          el('button', {
            class: 'small',
            onclick: async () => {
              try {
                await navigator.clipboard.writeText(url);
                toast('Address copied.', 'success');
              } catch {
                toast('Your browser would not let VisionMesh copy that. Select and copy it by hand.', 'error');
              }
            },
          }, 'Copy')))),
      el('p', { class: 'faint', style: { fontSize: '0.8rem', marginTop: '10px' } },
        `Server name: ${network.hostName} · Port: ${network.port}`)),

    el('div', { class: 'card' },
      el('h2', {}, 'Server'),
      el('dl', { class: 'kv' },
        el('dt', {}, 'Platform'), el('dd', {}, system.platform),
        el('dt', {}, 'Version'), el('dd', {}, system.version),
        el('dt', {}, 'Processor use'), el('dd', {},
          system.cpuPercent !== null && system.cpuPercent !== undefined ? `${system.cpuPercent}%` : 'measuring…'),
        el('dt', {}, 'Memory used'), el('dd', {}, `${Math.round(system.processMemoryBytes / 1024 / 1024)} MB`),
        el('dt', {}, 'Cameras'), el('dd', {}, `${system.camerasOnline} of ${system.cameraCount} online`),
        el('dt', {}, 'Devices'), el('dd', {}, `${system.devicesOnline} of ${system.deviceCount} connected`),
        el('dt', {}, 'ffmpeg'), el('dd', {},
          system.ffmpegAvailable ? `available (${system.ffmpegVersion})` : 'not installed'))),

    el('div', { class: 'card', style: { padding: 0 } },
      el('div', { style: { padding: '16px 16px 0' } }, el('h2', {}, 'Network interfaces')),
      el('div', { class: 'table-wrap' },
        el('table', {},
          el('thead', {}, el('tr', {},
            el('th', {}, 'Interface'),
            el('th', {}, 'Addresses'),
            el('th', {}, 'Gateway'),
            el('th', {}, 'Speed'),
            el('th', {}, ''))),
          el('tbody', {}, ...network.interfaces.map((nic) => el('tr', {},
            el('td', {},
              el('div', {}, nic.name),
              el('div', { class: 'faint', style: { fontSize: '0.78rem' } }, nic.description)),
            el('td', { class: 'mono' }, nic.addresses.join(', ')),
            el('td', { class: 'mono faint' }, nic.gateway || '—'),
            el('td', { class: 'faint nowrap' }, nic.speedBitsPerSecond ? formatSpeed(nic.speedBitsPerSecond) : '—'),
            el('td', {},
              !nic.up ? el('span', { class: 'badge offline' }, 'Down') : null,
              nic.isLikelyVpn ? el('span', { class: 'badge' }, 'VPN or tunnel') : null))))))),

    notice('info', 'Reaching VisionMesh from outside your home',
      'Do not forward a port to this server. Use a private network such as Tailscale or WireGuard instead: '
      + 'your phone joins the same private network and reaches VisionMesh exactly as if it were at home, '
      + 'without exposing anything to the internet. The Help section has a step-by-step guide.'));
}

function formatSpeed(bitsPerSecond) {
  if (bitsPerSecond >= 1_000_000_000) return `${(bitsPerSecond / 1_000_000_000).toFixed(1)} Gbit/s`;
  if (bitsPerSecond >= 1_000_000) return `${Math.round(bitsPerSecond / 1_000_000)} Mbit/s`;
  return `${Math.round(bitsPerSecond / 1000)} kbit/s`;
}
