/*
  Checks the browser's frame header against the fixed byte sequence the C# side is also tested
  against (VisionMesh.Tests.FrameHeaderTests).

  Neither implementation is checked against the other directly - both are checked against the same
  literal. That means a change to either one fails a test, rather than the two quietly drifting
  together into a shape the server no longer understands.

  Run from the repository root:
      node scripts/verify-frame-header.mjs
*/

import { buildFrame, FRAME_HEADER_SIZE, FRAME_FLAG_NATIVE_JPEG } from '../web/dashboard/js/frame.js';

// slot 0x1234, sequence 0xDEADBEEF, timestamp 1_700_000_000_123 ms, 1920x1080, native JPEG.
const EXPECTED_HEADER = [
  0x56, 0x4d, 0x46, 0x31,             // "VMF1"
  0x01,                               // payload: JPEG
  0x01,                               // flags: native JPEG
  0x34, 0x12,                         // slot 0x1234, little endian
  0xef, 0xbe, 0xad, 0xde,             // sequence 0xDEADBEEF, little endian
  0x7b, 0x68, 0xe5, 0xcf, 0x8b, 0x01, 0x00, 0x00,   // 1700000000123 ms, little endian int64
  0x80, 0x07,                         // width 1920
  0x38, 0x04,                         // height 1080
];

const payload = new Uint8Array([0xff, 0xd8, 0xff, 0xd9]);

const frame = new Uint8Array(buildFrame(0x1234, 0xdeadbeef, payload, 1920, 1080, {
  timestampMs: 1_700_000_000_123,
  flags: FRAME_FLAG_NATIVE_JPEG,
}));

let failures = 0;

if (frame.length !== FRAME_HEADER_SIZE + payload.length) {
  console.log(`  FAIL  frame length is ${frame.length}, expected ${FRAME_HEADER_SIZE + payload.length}`);
  failures++;
}

for (let i = 0; i < EXPECTED_HEADER.length; i++) {
  if (frame[i] !== EXPECTED_HEADER[i]) {
    console.log(`  FAIL  header byte ${i} is 0x${frame[i].toString(16).padStart(2, '0')}, `
              + `expected 0x${EXPECTED_HEADER[i].toString(16).padStart(2, '0')}`);
    failures++;
  }
}

for (let i = 0; i < payload.length; i++) {
  if (frame[FRAME_HEADER_SIZE + i] !== payload[i]) {
    console.log(`  FAIL  payload byte ${i} was not copied through`);
    failures++;
  }
}

if (failures === 0) {
  console.log(`  ok    header is ${FRAME_HEADER_SIZE} bytes and matches the wire contract`);
  console.log(`  ok    payload follows the header unchanged`);
  console.log('\nThe browser frame header matches the contract the server parses.');
  process.exit(0);
}

console.log(`\n${failures} mismatch(es). The browser and server frame headers have diverged.`);
process.exit(1);
