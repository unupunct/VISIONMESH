/* Sign-in screen. */

import { api } from '../api.js';
import { el, clear, field, textInput, notice } from '../ui.js';

export function renderLogin(status, onSuccess) {
  const messages = el('div');

  const username = textInput({ placeholder: 'Username', autocomplete: 'username', name: 'username' });
  const password = el('input', { type: 'password', placeholder: 'Password', autocomplete: 'current-password', name: 'password' });

  const submit = el('button', { class: 'primary', type: 'submit', style: { width: '100%', justifyContent: 'center' } }, 'Sign in');

  const form = el('form', {
    onsubmit: async (event) => {
      event.preventDefault();
      clear(messages);
      submit.disabled = true;
      submit.textContent = 'Signing in…';

      try {
        await api.login(username.value, password.value);
        onSuccess();
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
        password.value = '';
        password.focus();
        submit.disabled = false;
        submit.textContent = 'Sign in';
      }
    },
  },
    messages,
    field('Username', username),
    field('Password', password),
    submit);

  const screen = el('div', { class: 'auth-screen' },
    el('div', { class: 'auth-card card' },
      el('div', { class: 'logo' },
        el('img', { src: 'img/visionmesh-256.png', alt: '', width: 56, height: 56 }),
        el('div', { class: 'wordmark' }, 'VISIONMESH'),
        el('div', { class: 'tag' }, status.serverName || 'Self-hosted camera platform')),
      form));

  document.body.appendChild(screen);
  username.focus();
  return screen;
}
