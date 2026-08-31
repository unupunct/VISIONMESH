/*
  Settings: server-wide configuration and user accounts.

  Advanced fields live behind a toggle rather than on a separate page, so a technician can reach
  them in one click while a normal user never sees a path to an ffmpeg binary.
*/

import { api } from '../api.js';
import { store } from '../store.js';
import {
  el, clear, field, textInput, select, notice, toast, loading,
  openModal, confirmDialog, formatDateTime, explain, mount } from '../ui.js';

export async function renderSettings(content) {
  clear(content).appendChild(loading());

  const [settings, capabilities] = await Promise.all([api.settings(), api.capabilities()]);
  const isAdministrator = store.user?.role === 'Administrator';

  const messages = el('div');
  const advancedPanel = el('div', { hidden: !settings.advancedMode });

  const serverName = textInput({ value: settings.serverName, disabled: !isAdministrator });
  const recordingsPath = textInput({ value: settings.recordingsPath, disabled: !isAdministrator });
  const retentionDays = textInput({ type: 'number', min: '0', max: '3650', value: String(settings.retentionDays), disabled: !isAdministrator });
  const storageLimitGb = textInput({ type: 'number', min: '0', value: String(settings.storageLimitGb), disabled: !isAdministrator });
  const motionSensitivity = el('input', { type: 'range', min: '1', max: '100', value: String(settings.motionSensitivity), disabled: !isAdministrator });
  const ffmpegPath = textInput({ value: settings.ffmpegPath || '', placeholder: 'Found automatically', disabled: !isAdministrator });

  const advancedToggle = el('input', {
    type: 'checkbox',
    checked: settings.advancedMode,
    disabled: !isAdministrator,
    onchange: async (event) => {
      advancedPanel.hidden = !event.target.checked;
      if (!isAdministrator) return;
      try { await api.saveSettings({ advancedMode: event.target.checked }); } catch { /* saved with the rest */ }
    },
  });

  const testPath = el('button', {
    disabled: !isAdministrator,
    onclick: async () => {
      testPath.disabled = true;
      clear(messages);
      try {
        const result = await api.testStoragePath(recordingsPath.value);
        messages.appendChild(result.writable
          ? notice('success', null, 'VisionMesh can write to that folder.')
          : notice('error', 'That folder cannot be used', result.error));
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
      } finally {
        testPath.disabled = false;
      }
    },
  }, 'Test this folder');

  const save = el('button', {
    class: 'primary',
    disabled: !isAdministrator,
    onclick: async () => {
      clear(messages);
      save.disabled = true;

      try {
        await api.saveSettings({
          serverName: serverName.value.trim(),
          recordingsPath: recordingsPath.value.trim(),
          retentionDays: Number(retentionDays.value),
          storageLimitGb: Number(storageLimitGb.value),
          motionSensitivity: Number(motionSensitivity.value),
          ffmpegPath: ffmpegPath.value.trim() || null,
          advancedMode: advancedToggle.checked,
        });
        toast('Settings saved.', 'success');
      } catch (error) {
        messages.appendChild(notice('error', 'Settings could not be saved', error.message));
      } finally {
        save.disabled = false;
      }
    },
  }, 'Save settings');

  mount(advancedPanel, 
    el('h3', { style: { marginTop: '18px' } }, 'Advanced'),
    el('div', { class: 'field' },
      el('div', { class: 'spread' },
        el('label', {}, 'ffmpeg location'),
        explain('ffmpeg',
          'ffmpeg is a separate program VisionMesh uses to talk to network cameras and to write recordings.\n\n'
          + 'VisionMesh looks for it automatically. Set a path here only if you installed it somewhere unusual '
          + 'and VisionMesh cannot find it.\n\n'
          + 'USB cameras and phone cameras work without ffmpeg. Network cameras and recording do not.')),
      ffmpegPath,
      el('div', { class: 'hint' },
        capabilities.ffmpeg.available
          ? `Currently using ${capabilities.ffmpeg.version} at ${capabilities.ffmpeg.path}`
          : 'ffmpeg was not found. Network cameras and recording are switched off until it is installed.')),
    field('Total storage limit (GB)', storageLimitGb,
      '0 means no limit beyond the retention period. When the limit is reached, the oldest recordings are deleted.'),
    el('div', { class: 'field' },
      el('div', { class: 'spread' },
        el('label', {}, 'Default motion sensitivity'),
        explain('Motion sensitivity',
          'Motion detection compares each frame with the one before it and looks at how much changed.\n\n'
          + 'Higher sensitivity notices smaller movements, but also reacts more often to shadows, rain, '
          + 'headlights and insects.\n\n'
          + 'Lower sensitivity only reacts to obvious movement. Start in the middle and adjust if you get '
          + 'too many or too few recordings.')),
      motionSensitivity,
      el('div', { class: 'hint' }, 'Individual cameras can override this in their own settings.')));

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Settings'),
        el('p', { class: 'subtitle' }, 'How this server behaves.'))),

    !isAdministrator ? notice('info', 'Read only', 'Only an administrator can change these settings.') : null,
    messages,

    el('div', { class: 'card' },
      el('h2', {}, 'Server'),
      field('Server name', serverName, 'Shown in the app, on paired phones, and in Home Assistant.'),

      el('h3', { style: { marginTop: '18px' } }, 'Recordings'),
      field('Recordings folder', recordingsPath),
      el('div', { style: { marginBottom: '14px' } }, testPath),
      field('Keep recordings for (days)', retentionDays, '0 keeps everything until the disk fills up.'),

      el('div', { class: 'checkbox-row', style: { marginTop: '16px' } },
        advancedToggle,
        el('label', {}, 'Show advanced settings')),

      advancedPanel,

      el('div', { style: { marginTop: '20px' } }, save)),

    isAdministrator ? await usersCard() : null,
    isAdministrator ? auditCard() : null);
}

async function usersCard() {
  const list = el('div');
  const card = el('div', { class: 'card' },
    el('div', { class: 'spread', style: { marginBottom: '12px' } },
      el('h2', { style: { margin: 0 } }, 'Users'),
      el('button', { class: 'small primary', onclick: () => openUserEditor(null, build) }, 'Add user')),
    list);

  async function build() {
    clear(list).appendChild(loading());
    try {
      const users = await api.users();
      clear(list).appendChild(el('div', { class: 'table-wrap' },
        el('table', {},
          el('thead', {}, el('tr', {},
            el('th', {}, 'Username'),
            el('th', {}, 'Role'),
            el('th', {}, 'Last signed in'),
            el('th', {}))),
          el('tbody', {}, ...users.map((user) => el('tr', {},
            el('td', {},
              user.username,
              user.disabled ? el('span', { class: 'badge', style: { marginLeft: '8px' } }, 'Disabled') : null,
              user.id === store.user?.id ? el('span', { class: 'faint', style: { marginLeft: '8px' } }, '(you)') : null),
            el('td', {}, roleLabel(user.role)),
            el('td', { class: 'faint' }, user.lastLoginUtc ? formatDateTime(user.lastLoginUtc) : 'never'),
            el('td', { class: 'right nowrap' },
              el('button', { class: 'small', onclick: () => openUserEditor(user, build) }, 'Edit'),
              ' ',
              user.id === store.user?.id
                ? null
                : el('button', {
                    class: 'small danger',
                    onclick: async () => {
                      if (!await confirmDialog({
                        title: `Delete ${user.username}?`,
                        message: 'They will be signed out immediately and will not be able to sign in again.',
                        confirmLabel: 'Delete user',
                        danger: true,
                      })) return;

                      try {
                        await api.deleteUser(user.id);
                        toast('User deleted.', 'success');
                        build();
                      } catch (error) {
                        toast(error.message, 'error');
                      }
                    },
                  }, 'Delete'))))))));
    } catch (error) {
      clear(list).appendChild(notice('error', null, error.message));
    }
  }

  await build();
  return card;
}

function roleLabel(role) {
  return {
    Administrator: 'Administrator — can change everything',
    Operator: 'Operator — can view and control cameras',
    Viewer: 'Viewer — can only watch',
  }[role] || role;
}

function openUserEditor(user, onSaved) {
  const messages = el('div');
  const isNew = user === null;

  const username = textInput({ value: user?.username || '', disabled: !isNew });
  const password = el('input', { type: 'password', autocomplete: 'new-password' });
  const role = select([
    { value: 'Viewer', label: 'Viewer — can only watch cameras', selected: user?.role === 'Viewer' },
    { value: 'Operator', label: 'Operator — can also record, pause and control cameras', selected: user?.role === 'Operator' },
    { value: 'Administrator', label: 'Administrator — can change everything', selected: user?.role === 'Administrator' },
  ]);
  const disabled = el('input', { type: 'checkbox', checked: user?.disabled || false });

  const save = el('button', {
    class: 'primary',
    onclick: async () => {
      clear(messages);
      save.disabled = true;

      try {
        if (isNew) {
          await api.createUser({ username: username.value.trim(), password: password.value, role: role.value });
          toast('User created.', 'success');
        } else {
          await api.updateUser(user.id, {
            password: password.value || null,
            role: role.value,
            disabled: disabled.checked,
          });
          toast('User updated.', 'success');
        }
        handle.close();
        onSaved();
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
        save.disabled = false;
      }
    },
  }, isNew ? 'Create user' : 'Save changes');

  const handle = openModal({
    title: isNew ? 'Add user' : `Edit ${user.username}`,
    body: el('div', {},
      messages,
      field('Username', username),
      field(isNew ? 'Password' : 'New password', password,
        isNew ? 'At least 10 characters.' : 'Leave blank to keep the current password.'),
      field('Role', role),
      isNew ? null : el('div', { class: 'checkbox-row' }, disabled, el('label', {}, 'Disable this account'))),
    footer: [el('button', { onclick: () => handle.close() }, 'Cancel'), save],
  });
}

function auditCard() {
  const list = el('div');

  const card = el('div', { class: 'card' },
    el('div', { class: 'spread', style: { marginBottom: '12px' } },
      el('h2', { style: { margin: 0 } }, 'Security log'),
      el('button', { class: 'small', onclick: load }, 'Refresh')),
    el('p', { class: 'faint', style: { fontSize: '0.82rem' } },
      'Sign-ins, camera changes, pairing and privacy mode. Kept so you can see who did what.'),
    list);

  async function load() {
    clear(list).appendChild(loading());
    try {
      const entries = await api.auditLog(60);
      clear(list).appendChild(el('div', { class: 'table-wrap' },
        el('table', {},
          el('thead', {}, el('tr', {},
            el('th', {}, 'When'), el('th', {}, 'Who'), el('th', {}, 'Action'), el('th', {}, 'Detail'))),
          el('tbody', {}, ...entries.map((entry) => el('tr', {},
            el('td', { class: 'nowrap faint' }, formatDateTime(entry.timestampUtc)),
            el('td', {}, entry.username || '—'),
            el('td', { class: 'mono' }, entry.action),
            el('td', { class: 'faint' }, [entry.detail, entry.address].filter(Boolean).join(' · '))))))));
    } catch (error) {
      clear(list).appendChild(notice('error', null, error.message));
    }
  }

  load();
  return card;
}
