/*
  Home Assistant integration settings.

  Two separate things live here and the page is careful to keep them distinct:

   - The VisionMesh integration inside Home Assistant, which is what makes cameras appear as
     camera entities with live video. It runs inside HA and pulls from this server.
   - MQTT discovery, which publishes lightweight state (online, motion, frame rate, privacy)
     so those values are usable in automations without the integration polling for them.

  MQTT deliberately does not carry video. It can, technically, and every frame would then be a
  round trip through the broker.
*/

import { api } from '../api.js';
import { el, clear, field, textInput, notice, toast, loading, explain, mount } from '../ui.js';

export async function renderHomeAssistant(content) {
  clear(content).appendChild(loading());

  const config = await api.homeAssistant();
  const messages = el('div');
  const statusPanel = el('div');

  const url = textInput({ value: config.url || '', placeholder: 'http://homeassistant.local:8123' });
  const token = el('input', {
    type: 'password',
    placeholder: config.hasToken ? 'A token is saved. Leave blank to keep it.' : 'Long-lived access token',
  });

  const mqttEnabled = el('input', { type: 'checkbox', checked: config.mqtt.enabled });
  const mqttHost = textInput({ value: config.mqtt.host || '', placeholder: 'homeassistant.local' });
  const mqttPort = textInput({ type: 'number', value: String(config.mqtt.port || 1883), min: '1', max: '65535' });
  const mqttUsername = textInput({ value: config.mqtt.username || '' });
  const mqttPassword = el('input', {
    type: 'password',
    placeholder: config.mqtt.hasPassword ? 'A password is saved. Leave blank to keep it.' : '',
  });
  const mqttPrefix = textInput({ value: config.mqtt.discoveryPrefix || 'homeassistant' });

  function renderStatus() {
    mount(clear(statusPanel), 
      el('div', { class: 'stat-strip' },
        stat(config.mqtt.connected ? 'Connected' : (config.mqtt.enabled ? 'Not connected' : 'Off'),
             'MQTT', config.mqtt.connected ? 'good' : config.mqtt.enabled ? 'warn' : ''),
        stat(String(config.cameraCount), 'Cameras available'),
        stat(config.mqtt.connected ? String(config.mqtt.publishedEntities) : '—', 'Entities published')),
      config.mqtt.lastError ? notice('warn', 'Last MQTT problem', config.mqtt.lastError) : null);
  }

  const testButton = el('button', {
    onclick: async () => {
      testButton.disabled = true;
      testButton.textContent = 'Testing…';
      clear(messages);

      try {
        const result = await api.testHomeAssistant({ url: url.value.trim(), token: token.value });
        messages.appendChild(result.connected
          ? notice('success', 'Connected to Home Assistant',
              `Found ${result.locationName || 'your Home Assistant'}${result.version ? `, version ${result.version}` : ''}.`)
          : notice('error', 'Could not connect', result.error));
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
      } finally {
        testButton.disabled = false;
        testButton.textContent = 'Test connection';
      }
    },
  }, 'Test connection');

  const save = el('button', {
    class: 'primary',
    onclick: async () => {
      clear(messages);
      save.disabled = true;

      try {
        await api.saveHomeAssistant({
          url: url.value.trim(),
          // An empty box means "keep the stored secret", which is why null and "" differ here.
          token: token.value ? token.value : null,
          mqttEnabled: mqttEnabled.checked,
          mqttHost: mqttHost.value.trim(),
          mqttPort: Number(mqttPort.value),
          mqttUsername: mqttUsername.value.trim(),
          mqttPassword: mqttPassword.value ? mqttPassword.value : null,
          mqttDiscoveryPrefix: mqttPrefix.value.trim(),
        });
        toast('Home Assistant settings saved.', 'success');
        token.value = '';
        mqttPassword.value = '';
      } catch (error) {
        messages.appendChild(notice('error', null, error.message));
      } finally {
        save.disabled = false;
      }
    },
  }, 'Save');

  renderStatus();

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Home Assistant'),
        el('p', { class: 'subtitle' }, 'Make your cameras part of your smart home.'))),

    messages,
    statusPanel,

    el('div', { class: 'card' },
      el('h2', {}, 'Step 1 — Install the VisionMesh integration'),
      el('p', { class: 'dim' },
        'This is what makes your cameras appear in Home Assistant with live video, snapshots and controls. '
        + 'It runs inside Home Assistant and connects back to this server.'),
      el('ol', { class: 'dim', style: { lineHeight: '1.9' } },
        el('li', {}, 'Copy the ', el('code', { class: 'mono' }, 'custom_components/visionmesh'),
                     ' folder from the VisionMesh release into your Home Assistant ',
                     el('code', { class: 'mono' }, 'config/custom_components'), ' folder.'),
        el('li', {}, 'Restart Home Assistant.'),
        el('li', {}, 'Go to Settings → Devices & services → Add integration, and search for VisionMesh.'),
        el('li', {}, 'Enter this server address: ',
                     el('code', { class: 'mono' }, config.integration.serverUrl || 'this server')),
        el('li', {}, 'Sign in with a VisionMesh username and password. A Viewer account is enough for cameras; '
                   + 'use an Operator account if you want Home Assistant to control recording or privacy mode.'),
        el('li', {}, 'Your cameras appear as camera entities straight away.')),
      el('p', { class: 'faint', style: { fontSize: '0.82rem' } },
        'Nothing needs to be entered below for this to work. The fields below are only used to check the '
        + 'connection from this side and to publish extra sensors over MQTT.')),

    el('div', { class: 'card' },
      el('h2', {}, 'Check the connection'),
      el('p', { class: 'dim' },
        'Optional. Enter your Home Assistant address and a long-lived access token, and VisionMesh will '
        + 'confirm it can reach it. This is only a check; it does not send anything to Home Assistant.'),
      field('Home Assistant address', url),
      field('Long-lived access token', token,
        'In Home Assistant, click your profile at the bottom left, then Security, then create a long-lived access token.'),
      testButton),

    el('div', { class: 'card' },
      el('div', { class: 'spread' },
        el('h2', { style: { margin: 0 } }, 'Step 2 — MQTT sensors (optional)'),
        explain('MQTT discovery',
          'MQTT is a lightweight messaging system Home Assistant uses to learn about devices automatically.\n\n'
          + 'When you switch this on, VisionMesh publishes each camera’s state — online or offline, recording, '
          + 'frame rate, privacy mode — to your MQTT broker, and Home Assistant creates sensors for them without '
          + 'you writing any YAML.\n\n'
          + 'Live video does not travel over MQTT. Sending video through a message broker would make the broker '
          + 'the bottleneck for your whole smart home. Video comes from the VisionMesh integration instead.\n\n'
          + 'You only need this if you already run an MQTT broker, which usually means the Mosquitto add-on.')),
      el('p', { class: 'dim' },
        'Publishes camera state as Home Assistant sensors, so you can use them in automations.'),

      el('div', { class: 'checkbox-row', style: { marginBottom: '14px' } },
        mqttEnabled, el('label', {}, 'Publish camera state over MQTT')),

      el('div', { class: 'field-row' },
        field('Broker address', mqttHost),
        field('Port', mqttPort)),
      el('div', { class: 'field-row' },
        field('Username', mqttUsername, 'Leave blank if your broker allows anonymous access.'),
        field('Password', mqttPassword)),
      field('Discovery prefix', mqttPrefix, 'Leave this as homeassistant unless you have changed it in Home Assistant.'),

      el('div', { style: { marginTop: '8px' } }, save)),

    el('div', { class: 'card' },
      el('h2', {}, 'What you get in Home Assistant'),
      el('div', { class: 'grid-2' },
        el('div', {},
          el('h3', {}, 'From the integration'),
          el('ul', { class: 'dim', style: { fontSize: '0.88rem', lineHeight: '1.8' } },
            el('li', {}, 'A camera entity per camera, with live video and snapshots'),
            el('li', {}, 'Online and offline status'),
            el('li', {}, 'Motion status for cameras set to record on motion'),
            el('li', {}, 'Frame rate, bitrate and latency sensors'),
            el('li', {}, 'Privacy mode and recording switches'),
            el('li', {}, 'Pan, tilt and zoom buttons on cameras that support them'))),
        el('div', {},
          el('h3', {}, 'From MQTT, if enabled'),
          el('ul', { class: 'dim', style: { fontSize: '0.88rem', lineHeight: '1.8' } },
            el('li', {}, 'The same state values, published independently'),
            el('li', {}, 'Useful if you want automations to keep working even when the integration is reloading'),
            el('li', {}, 'Server-wide sensors: cameras online, cameras total'))))));
}

function stat(value, label, tone) {
  return el('div', { class: 'stat' },
    el('div', { class: `value ${tone || ''}` }, String(value)),
    el('div', { class: 'label' }, label));
}
