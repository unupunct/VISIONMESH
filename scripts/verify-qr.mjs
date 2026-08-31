/*
  Emits the QR matrices produced by web/dashboard/js/qr.js as JSON, for verify-qr.py to check.

  Run from the repository root:
      node scripts/verify-qr.mjs > qr.json
      python scripts/verify-qr.py qr.json
*/

import { encodeQr } from '../web/dashboard/js/qr.js';

const samples = process.argv.length > 2 ? process.argv.slice(2) : [
  'A',
  'HELLO',
  'visionmesh://pair?code=XY34-6789',
  'visionmesh://pair?code=ACDE-FGHJ&url=http%3A%2F%2F192.168.1.10%3A8088&name=Home%20Surveillance',
  'The quick brown fox jumps over the lazy dog 0123456789 and keeps going for a while to push the version up a few steps',
];

const output = {};
for (const text of samples) {
  output[text] = encodeQr(text).map((row) => row.join('')).join('\n');
}

console.log(JSON.stringify(output));
