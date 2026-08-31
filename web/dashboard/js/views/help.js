/*
  The built-in help centre.

  Written for someone who has never heard of RTSP, with a technical note at the end of each topic
  for someone who has. It lives inside the app rather than on a website because a self-hosted
  surveillance server is often installed exactly where there is no internet access.
*/

import { el, clear, mount } from '../ui.js';

const TOPICS = [
  {
    id: 'getting-started',
    title: 'Getting started',
    summary: 'What VisionMesh is and how the pieces fit together.',
    body: [
      ['What is VisionMesh?',
        'VisionMesh turns cameras of all kinds into one system you watch from one place. A webcam plugged into '
        + 'a computer, an old phone on a windowsill, and a proper security camera on your network all end up '
        + 'side by side on the same screen.'],
      ['The three pieces',
        'The server is the part you installed. It keeps the settings, the recordings and the users, and it is '
        + 'what your browser and phone connect to.\n\n'
        + 'An agent is a small program you install on another computer so its camera can be used. You only '
        + 'need one if you want to use a camera that is plugged into a different machine.\n\n'
        + 'Network cameras need neither: the server talks to them directly.'],
      ['Where to keep the server',
        'Anywhere that stays switched on: an old desktop, a mini PC, a NUC, or a home server. It does not need '
        + 'to be powerful. It does need enough disk space for however much footage you want to keep.'],
    ],
  },
  {
    id: 'adding-cameras',
    title: 'Adding a camera',
    summary: 'The five kinds of camera and how to add each.',
    body: [
      ['The short version',
        'Press Add camera, choose what kind of camera it is, and follow the steps. VisionMesh finds most cameras '
        + 'for you, so in most cases you are choosing from a list rather than typing anything.'],
      ['A webcam on this computer or another one',
        'Install the VisionMesh agent on the computer the camera is plugged into, pair it with this server, and '
        + 'the camera appears in the list. See "Using a computer as a camera".'],
      ['A phone',
        'Install VisionMesh on the phone, scan the pairing code, choose front or rear camera, give it a name and '
        + 'press Start camera. See "Using a phone as a camera".'],
      ['A security camera on your network',
        'Choose IP camera. VisionMesh asks every camera on the network to identify itself and lists the ones that '
        + 'answer. Pick yours, enter its username and password, and choose the picture quality.'],
      ['A camera that does not show up',
        'Choose RTSP and type the camera’s address. It is usually in the camera’s manual or its own app, and it '
        + 'looks like rtsp://192.168.1.50:554/stream1'],
      ['Advanced',
        'Discovery uses WS-Discovery, a multicast protocol, so it only reaches cameras on the same network segment. '
        + 'A camera on a separate VLAN will not answer and must be added by address. VisionMesh reads the camera’s '
        + 'ONVIF media profiles and offers each as a quality option; the first is normally the main stream and the '
        + 'rest are substreams.'],
    ],
  },
  {
    id: 'computer-camera',
    title: 'Using a computer as a camera',
    summary: 'Install the agent on Windows or Linux.',
    body: [
      ['What you need', 'A computer with a camera, on the same network as the server.'],
      ['Step by step',
        '1. Download the VisionMesh agent for that computer from the releases page.\n\n'
        + '2. On this server, open Devices and press Add device. A pairing code appears.\n\n'
        + '3. On the other computer, open a terminal in the folder you downloaded the agent to and run:\n'
        + '   VisionMesh.Agent pair\n\n'
        + '4. Enter this server’s address and the pairing code.\n\n'
        + '5. Start the agent by running it with no arguments.\n\n'
        + '6. Back here, press Add camera, choose USB or built-in camera, and pick the camera you want.'],
      ['Expected result',
        'The computer appears under Devices as connected, and its cameras are offered when you add a camera.'],
      ['If it does not work',
        'Run "VisionMesh.Agent list" on that computer. If it finds no cameras, the problem is between the camera '
        + 'and that computer, not with VisionMesh. On Windows, check Settings → Privacy & security → Camera and '
        + 'make sure desktop apps are allowed to use the camera.\n\n'
        + 'If the camera is listed but says it is in use, another program has it open. Close Teams, Zoom or the '
        + 'camera’s own app and try again.'],
      ['Advanced',
        'The Windows agent uses Media Foundation and the Linux agent uses V4L2. Both prefer a camera’s native '
        + 'MJPEG output when it has one, in which case frames are forwarded to the server without being decoded '
        + 'or re-encoded at all. If the camera cannot produce JPEG, the agent converts to RGB and encodes, which '
        + 'costs some CPU on that machine.'],
    ],
  },
  {
    id: 'phone-camera',
    title: 'Using a phone as a camera',
    summary: 'Turn an old phone into a security camera.',
    body: [
      ['What you need', 'A phone with the VisionMesh app, on the same Wi-Fi as the server, and a charger.'],
      ['Step by step',
        '1. Install VisionMesh on the phone.\n\n2. Open it and choose Scan server.\n\n'
        + '3. On this server, press Add camera and choose your phone’s type. A code appears.\n\n'
        + '4. Scan the code with the phone.\n\n5. Choose the front or rear camera.\n\n'
        + '6. Give it a name, such as Front door.\n\n7. Press Start camera.'],
      ['Expected result', 'The phone appears on your camera wall within a few seconds.'],
      ['Keep it plugged in',
        'Streaming video uses the camera, the processor and the radio continuously. A phone doing this will not '
        + 'last a day on battery, and will get warm. Treat a phone camera as a mains-powered device.\n\n'
        + 'The Low power profile — 720p at 15 frames per second — is much gentler and is the right choice for a '
        + 'phone that is going to run for weeks.'],
      ['Common problems',
        'If the phone stops streaming after a while, its operating system has probably put the app to sleep. '
        + 'On Android, exclude VisionMesh from battery optimisation. Both Android and iOS limit what an app may '
        + 'do in the background, and VisionMesh does not try to work around those limits.'],
    ],
  },
  {
    id: 'recording',
    title: 'Recording',
    summary: 'Continuous, motion, scheduled and manual recording.',
    body: [
      ['Choosing when to record',
        'Open a camera, press Settings, and choose under Recording.\n\n'
        + 'Record all the time keeps everything. It is the most reliable and uses the most disk.\n\n'
        + 'Record when something moves keeps far less, but only captures what the detector notices.\n\n'
        + 'Record on a schedule is useful for a camera you only care about overnight.\n\n'
        + 'Only when I press record leaves the camera live but recording nothing until you say so.'],
      ['How long recordings are kept',
        'Each camera has its own retention period. Once a recording is older than that, it is deleted '
        + 'automatically to make room. Set it to 0 to keep everything until the disk fills up.'],
      ['ffmpeg is required',
        'Recording needs a program called ffmpeg on the server. If it is missing, VisionMesh says so plainly and '
        + 'the recording controls are switched off rather than silently doing nothing. Install it with your system’s '
        + 'package manager: on Ubuntu, "sudo apt install ffmpeg".'],
      ['Advanced',
        'Network cameras recording continuously are written with the stream copied directly from the camera — no '
        + 're-encoding, full source quality, almost no processor use, and only one connection to the camera because '
        + 'the same ffmpeg process also produces the live view.\n\n'
        + 'Cameras whose source is already JPEG (agents and phones), and any camera recording on motion or on demand, '
        + 'are encoded to H.264 as they record. Motion and manual recording need to start and stop instantly, which '
        + 'a stream copy cannot do without restarting the connection.\n\n'
        + 'Recordings are ten-minute MP4 segments named by their start time, so the archive is readable straight from '
        + 'a file manager even without VisionMesh.'],
    ],
  },
  {
    id: 'motion',
    title: 'Motion detection',
    summary: 'How it works and how to stop false alarms.',
    body: [
      ['How it works',
        'VisionMesh compares each frame with the one before it and measures how much of the picture changed. '
        + 'If enough changed, for long enough, that is motion.'],
      ['Tuning it',
        'If you get too many recordings, lower the sensitivity. If you miss things, raise it.\n\n'
        + 'Rain, snow, moving shadows, headlights at night and insects near the lens are the usual causes of false '
        + 'alarms. Pointing the camera slightly downward, so less sky is in frame, helps more than any setting.'],
      ['What it is not',
        'This detects movement, not people. VisionMesh will not tell you a person was at the door, because it does '
        + 'not know. Anything claiming otherwise without a recognition model behind it is guessing.'],
      ['Advanced',
        'Detection runs on a one-eighth scale greyscale image recovered from each JPEG without fully decoding it, '
        + 'which keeps it cheap enough to run on every camera at once. A global brightness shift is subtracted before '
        + 'comparison, so a light switching on or the camera auto-exposing does not read as motion across the whole '
        + 'frame. Motion must persist across consecutive frames before it triggers.\n\n'
        + 'A few seconds of video from before the trigger are kept in memory and written into the recording, so clips '
        + 'start before the interesting thing entered the frame rather than after.'],
    ],
  },
  {
    id: 'remote-access',
    title: 'Watching from outside your home',
    summary: 'The safe way to reach VisionMesh remotely.',
    body: [
      ['Do not forward a port',
        'The usual advice is to forward a port on your router. Do not. That puts your cameras on the public internet, '
        + 'where they will be found and probed within hours. Security cameras exposed this way are one of the most '
        + 'common sources of leaked home footage.'],
      ['Use a private network instead',
        'Tailscale and WireGuard both create a private network that your phone joins. From your phone’s point of view '
        + 'it is at home, so VisionMesh works exactly as it does on your Wi-Fi, and nothing is exposed to the internet.'],
      ['Tailscale, step by step',
        '1. Install Tailscale on the VisionMesh server and sign in.\n\n'
        + '2. Install Tailscale on your phone and sign in with the same account.\n\n'
        + '3. On the server, run "tailscale ip -4" to see its Tailscale address.\n\n'
        + '4. In VisionMesh on your phone, use that address instead of the home one.\n\n'
        + 'It now works from anywhere, over mobile data, with no router changes at all.'],
      ['Expected result',
        'The dashboard opens over mobile data with the cameras live, and nothing about your home network is reachable '
        + 'from the public internet.'],
      ['Advanced',
        'The Network page lists every address this server has, and marks interfaces that look like a VPN or tunnel. '
        + 'VisionMesh does not hard-code any VPN address range, because they differ per provider and change; it reports '
        + 'what the operating system says about each interface.'],
    ],
  },
  {
    id: 'home-assistant',
    title: 'Home Assistant',
    summary: 'Bring your cameras into your smart home.',
    body: [
      ['What you get',
        'Each VisionMesh camera becomes a camera entity in Home Assistant, with live video and snapshots, plus '
        + 'sensors for online status, motion, frame rate and recording state. From there you can build automations: '
        + 'motion at the front door turns on the porch light and sends a notification.'],
      ['Setting it up',
        '1. Copy the custom_components/visionmesh folder from the VisionMesh release into your Home Assistant '
        + 'config/custom_components folder.\n\n2. Restart Home Assistant.\n\n'
        + '3. Settings → Devices & services → Add integration → VisionMesh.\n\n'
        + '4. Enter this server’s address and a VisionMesh username and password.\n\n'
        + '5. Choose which cameras to expose.'],
      ['Which account to use',
        'A Viewer account is enough to see cameras. Use an Operator account if you want Home Assistant to be able '
        + 'to start recording, turn on privacy mode, or move a camera.'],
      ['Advanced',
        'Entity unique IDs come from the VisionMesh camera id, which never changes, so renaming a camera or changing '
        + 'its IP address does not orphan the entity.\n\n'
        + 'MQTT discovery is available separately for state, and is entirely optional. Video never travels over MQTT.'],
    ],
  },
  {
    id: 'troubleshooting',
    title: 'When something is wrong',
    summary: 'A camera is offline, or the picture is bad.',
    body: [
      ['Start with Fix camera',
        'Open the camera and press Fix camera. VisionMesh checks the whole chain — device connected, camera present, '
        + 'network reachable, credentials accepted, video arriving, disk writable — and tells you the first thing that '
        + 'is actually wrong, in plain language.'],
      ['The camera says Offline',
        'For a camera on another computer, that computer is switched off, asleep, or the agent is not running.\n\n'
        + 'For a network camera, either the camera is off, its address changed, or its password is wrong. Fix camera '
        + 'tells you which.'],
      ['The picture keeps freezing or breaking up',
        'Almost always the network. Wi-Fi cameras far from the access point are the usual culprit. Lower the picture '
        + 'size or the frame rate in the camera’s settings, or move it to a wired connection.\n\n'
        + 'For RTSP cameras, changing the connection type to TCP in the camera’s settings often fixes tearing.'],
      ['Everything is slow and the server is busy',
        'Check the Network page for processor use. Cameras recording on motion, and any camera that is not sending '
        + 'JPEG natively, cost processing power. Lowering the frame rate helps more than lowering the resolution.'],
      ['A camera disappeared after a reboot',
        'If it was a USB camera, Windows or Linux may have given it a different device path. Remove it and add it '
        + 'again; VisionMesh identifies cameras by their device path, which is stable for most cameras but not all.'],
    ],
  },
  {
    id: 'security',
    title: 'Security and privacy',
    summary: 'What VisionMesh does with your footage, and what it will never do.',
    body: [
      ['Your footage stays yours',
        'VisionMesh has no cloud. Video never leaves your network unless you deliberately send it somewhere. There is '
        + 'no account to create, no telemetry, and nothing phones home.'],
      ['Privacy mode',
        'Every camera has a privacy mode that genuinely stops capture and recording, not just hides the picture. '
        + 'While it is on, the camera is not streaming to anyone, is not recording, and anyone opening it is told why.'],
      ['What VisionMesh will not do',
        'It will never hide that a camera is active, never bypass an operating system’s camera indicator or permission '
        + 'prompt, and never record without that being visible in the interface. If a camera is capturing, the dashboard '
        + 'says so.'],
      ['Passwords and tokens',
        'User passwords are stored hashed with PBKDF2 and are never recoverable. Camera passwords are encrypted at rest. '
        + 'Pairing codes are single use and expire in minutes. Device tokens are stored only as hashes, so a copy of the '
        + 'database does not let anyone impersonate a device.'],
      ['Roles',
        'Viewer can only watch. Operator can also record, pause and move cameras. Administrator can change everything. '
        + 'Give people the smallest role that lets them do what they need.'],
      ['Use HTTPS if you can',
        'On a home network over HTTP, traffic between your browser and the server is unencrypted. Anyone on the same '
        + 'network could watch it. Putting VisionMesh behind a reverse proxy with a certificate, or reaching it only '
        + 'over a private network such as Tailscale, closes that off.'],
    ],
  },
];

export async function renderHelp(content, [topicId]) {
  const topic = TOPICS.find((entry) => entry.id === topicId);

  if (topic) {
    mount(clear(content), 
      el('div', { class: 'page-head' },
        el('div', {},
          el('h1', {}, topic.title),
          el('p', { class: 'subtitle' }, topic.summary)),
        el('button', { onclick: () => { location.hash = '#/help'; } }, '← All help')),
      ...topic.body.map(([heading, text]) => el('div', { class: 'card' },
        el('h2', {}, heading),
        ...text.split('\n\n').map((paragraph) => el('p', { class: 'dim', style: { whiteSpace: 'pre-line' } }, paragraph)))));
    return;
  }

  mount(clear(content), 
    el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, 'Help'),
        el('p', { class: 'subtitle' }, 'Everything you need, written for people who are not surveillance engineers.'))),

    el('div', { class: 'grid-2' },
      ...TOPICS.map((entry) => el('button', {
        class: 'source-option',
        onclick: () => { location.hash = `#/help/${entry.id}`; },
      },
        el('div', {},
          el('div', { class: 'title' }, entry.title),
          el('div', { class: 'desc' }, entry.summary))))),

    el('div', { class: 'card', style: { marginTop: '16px' } },
      el('h2', {}, 'Still stuck?'),
      el('p', { class: 'dim' },
        'The full documentation, including everything above plus deeper technical detail, ships in the docs folder of '
        + 'the VisionMesh release and is on the project page.'),
      el('p', { class: 'dim' },
        'The API is documented at ', el('a', { href: '/api/docs', target: '_blank', rel: 'noopener' }, '/api/docs'),
        ' on this server.')));
}
