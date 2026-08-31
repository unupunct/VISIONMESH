/* Account menu: who you are signed in as, password change, sign out. */

import { api } from '../api.js';
import { store } from '../store.js';
import { el, openModal, field, notice, clear, toast } from '../ui.js';

export function showAccountMenu() {
  const messages = el('div');

  const currentPassword = el('input', { type: 'password', autocomplete: 'current-password' });
  const newPassword = el('input', { type: 'password', autocomplete: 'new-password' });
  const confirmPassword = el('input', { type: 'password', autocomplete: 'new-password' });

  const changeButton = el('button', {
    class: 'primary',
    onclick: async () => {
      clear(messages);

      if (newPassword.value !== confirmPassword.value) {
        messages.appendChild(notice('warn', null, 'The two new passwords do not match.'));
        return;
      }

      changeButton.disabled = true;
      try {
        await api.changeOwnPassword(currentPassword.value, newPassword.value);
        // Changing a password ends every session, including this one, so a reload lands on login.
        toast('Password changed. Please sign in again.', 'success');
        setTimeout(() => location.reload(), 1200);
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
        changeButton.disabled = false;
      }
    },
  }, 'Change password');

  const handle = openModal({
    title: 'Your account',
    body: el('div', {},
      el('dl', { class: 'kv', style: { marginBottom: '18px' } },
        el('dt', {}, 'Signed in as'), el('dd', {}, store.user?.username || '—'),
        el('dt', {}, 'Role'), el('dd', {}, store.user?.role || '—')),
      el('h3', {}, 'Change your password'),
      messages,
      field('Current password', currentPassword),
      field('New password', newPassword, 'At least 10 characters.'),
      field('Confirm new password', confirmPassword),
      changeButton),
    footer: [
      el('button', { class: 'danger', onclick: async () => {
        try { await api.logout(); } finally { location.reload(); }
      } }, 'Sign out'),
      el('button', { onclick: () => handle.close() }, 'Close'),
    ],
  });
}
