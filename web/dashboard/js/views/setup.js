/*
  First-run wizard.

  Five short steps, each asking one thing, with a working default already filled in. Someone who
  presses Continue five times ends up with a correctly configured server; the questions exist for
  the people who want to answer them.
*/

import { api } from '../api.js';
import { el, clear, field, textInput, select, notice, toast, mount } from '../ui.js';

export function renderSetup(status, onComplete) {
  const state = {
    step: 0,
    serverName: status.serverName || 'Home Surveillance',
    adminUsername: '',
    adminPassword: '',
    confirmPassword: '',
    recordingsPath: status.suggestedRecordingsPath || '',
    retentionDays: 7,
  };

  const screen = el('div', { class: 'auth-screen' });
  const card = el('div', { class: 'auth-card card' });
  screen.appendChild(card);
  document.body.appendChild(screen);

  const steps = [welcomeStep, serverNameStep, accountStep, storageStep, readyStep];

  function render() {
    clear(card);

    card.appendChild(el('div', { class: 'logo' },
      el('img', { src: 'img/visionmesh-256.png', alt: '', width: 56, height: 56 }),
      el('div', { class: 'wordmark' }, 'VISIONMESH')));

    if (state.step > 0) {
      card.appendChild(el('div', { class: 'steps' },
        ...steps.slice(1).map((_, index) => el('div', { class: `step${index < state.step ? ' done' : ''}` }))));
    }

    steps[state.step](card, render, state, onComplete);
  }

  render();
  return screen;
}

function welcomeStep(card, render, state) {
  mount(card, 
    el('h1', {}, 'Welcome to VisionMesh'),
    el('p', { class: 'dim' }, 'Let us get your surveillance system running. This takes about a minute.'),
    el('p', { class: 'faint', style: { fontSize: '0.85rem' } },
      'You will choose a name for this server, create an administrator account, and pick where recordings are kept. '
      + 'Everything here can be changed later.'),
    el('div', { style: { marginTop: '20px' } },
      el('button', { class: 'primary', onclick: () => { state.step = 1; render(); } }, 'Start setup')));
}

function serverNameStep(card, render, state) {
  const input = textInput({
    value: state.serverName,
    placeholder: 'Home Surveillance',
    maxlength: 60,
    oninput: (event) => { state.serverName = event.target.value; },
  });

  mount(card, 
    el('h1', {}, 'Name this server'),
    el('p', { class: 'dim' }, 'This name appears in the app, on phones that pair with it, and in Home Assistant.'),
    field('Server name', input),
    navigation(render, state, {
      canContinue: () => state.serverName.trim().length > 0,
      error: 'Enter a name for this server.',
    }));
}

function accountStep(card, render, state) {
  const messages = el('div');

  const username = textInput({
    value: state.adminUsername,
    placeholder: 'admin',
    autocomplete: 'username',
    oninput: (event) => { state.adminUsername = event.target.value; },
  });

  const password = el('input', {
    type: 'password',
    value: state.adminPassword,
    autocomplete: 'new-password',
    oninput: (event) => { state.adminPassword = event.target.value; },
  });

  const confirm = el('input', {
    type: 'password',
    value: state.confirmPassword,
    autocomplete: 'new-password',
    oninput: (event) => { state.confirmPassword = event.target.value; },
  });

  mount(card, 
    el('h1', {}, 'Create your account'),
    el('p', { class: 'dim' }, 'This account can manage everything: cameras, users, storage and integrations.'),
    messages,
    field('Username', username),
    field('Password', password, 'At least 10 characters. A short phrase you will remember works well.'),
    field('Confirm password', confirm),
    navigation(render, state, {
      canContinue: () => {
        clear(messages);

        if (!state.adminUsername.trim()) {
          messages.appendChild(notice('warn', null, 'Choose a username.'));
          return false;
        }
        if (state.adminPassword.length < 10) {
          messages.appendChild(notice('warn', null, 'Use a password of at least 10 characters.'));
          return false;
        }
        if (state.adminPassword !== state.confirmPassword) {
          messages.appendChild(notice('warn', null, 'The two passwords do not match.'));
          return false;
        }
        return true;
      },
    }));
}

function storageStep(card, render, state) {
  const messages = el('div');

  const path = textInput({
    value: state.recordingsPath,
    oninput: (event) => { state.recordingsPath = event.target.value; },
  });

  const retention = select([
    { value: '1', label: '1 day' },
    { value: '3', label: '3 days' },
    { value: '7', label: '7 days', selected: true },
    { value: '14', label: '14 days' },
    { value: '30', label: '30 days' },
    { value: '90', label: '90 days' },
    { value: '0', label: 'Keep everything' },
  ], { onchange: (event) => { state.retentionDays = Number(event.target.value); } });

  const testButton = el('button', {
    onclick: async () => {
      testButton.disabled = true;
      clear(messages);
      try {
        const result = await api.testSetupPath(state.recordingsPath);
        messages.appendChild(result.writable
          ? notice('success', null, 'VisionMesh can write to that folder.')
          : notice('error', 'That folder cannot be used', result.error));
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
      } finally {
        testButton.disabled = false;
      }
    },
  }, 'Test this folder');

  mount(card, 
    el('h1', {}, 'Where should recordings go?'),
    el('p', { class: 'dim' }, 'Pick a drive with room to spare. You can change this later, and point it at a NAS or a second disk.'),
    messages,
    field('Recordings folder', path),
    el('div', { style: { marginBottom: '14px' } }, testButton),
    field('Keep recordings for', retention,
      'Older recordings are deleted automatically to make room. Nothing is deleted before this.'),
    navigation(render, state, { canContinue: () => state.recordingsPath.trim().length > 0 }));
}

function readyStep(card, render, state, onComplete) {
  const messages = el('div');

  const finish = el('button', {
    class: 'primary',
    onclick: async () => {
      finish.disabled = true;
      finish.textContent = 'Setting up…';
      clear(messages);

      try {
        await api.completeSetup({
          serverName: state.serverName.trim(),
          adminUsername: state.adminUsername.trim(),
          adminPassword: state.adminPassword,
          recordingsPath: state.recordingsPath.trim(),
          retentionDays: state.retentionDays,
        });

        toast('VisionMesh is ready.', 'success');
        onComplete();
      } catch (error) {
        messages.appendChild(notice('error', 'Setup could not be completed', error.message));
        finish.disabled = false;
        finish.textContent = 'Finish setup';
      }
    },
  }, 'Finish setup');

  mount(card, 
    el('h1', {}, 'Ready'),
    el('p', { class: 'dim' }, 'Check these over, then finish. You will be signed in straight away.'),
    messages,
    el('dl', { class: 'kv', style: { margin: '16px 0' } },
      el('dt', {}, 'Server name'), el('dd', {}, state.serverName),
      el('dt', {}, 'Administrator'), el('dd', {}, state.adminUsername),
      el('dt', {}, 'Recordings'), el('dd', { class: 'mono' }, state.recordingsPath),
      el('dt', {}, 'Keep for'), el('dd', {}, state.retentionDays === 0 ? 'Everything' : `${state.retentionDays} days`)),
    el('div', { class: 'spread' },
      el('button', { onclick: () => { state.step -= 1; render(); } }, 'Back'),
      finish));
}

function navigation(render, state, { canContinue, error }) {
  return el('div', { class: 'spread', style: { marginTop: '18px' } },
    state.step > 1
      ? el('button', { onclick: () => { state.step -= 1; render(); } }, 'Back')
      : el('span'),
    el('button', {
      class: 'primary',
      onclick: () => {
        if (canContinue && !canContinue()) {
          if (error) toast(error, 'error');
          return;
        }
        state.step += 1;
        render();
      },
    }, 'Continue'));
}
