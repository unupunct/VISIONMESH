/*
  The binary frame header, browser side.

  This is one half of a cross-language wire contract: the other half is
  VisionMesh.Core.Contracts.FrameHeader in C#. Both are checked against the same fixed byte
  sequence (scripts/verify-frame-header.mjs and FrameHeaderTests) so neither can drift without a
  test failing. Getting this wrong does not throw anywhere - the server simply drops every frame
  and the camera sits silently offline, which is exactly the kind of bug worth a contract test.

  Layout, little endian throughout:
     0..3   magic "VMF1"
     4      payload kind (1 = JPEG)
     5      flags (1 = the source produced JPEG itself and it was not re-encoded)
     6..7   slot        uint16
     8..11  sequence    uint32
     12..19 timestamp   int64, unix milliseconds UTC
     20..21 width       uint16
     22..23 height      uint16
*/

export const FRAME_HEADER_SIZE = 24;

export const FRAME_PAYLOAD_JPEG = 1;

export const FRAME_FLAG_NONE = 0;
export const FRAME_FLAG_NATIVE_JPEG = 1;

/**
 * Writes a frame header followed by the JPEG payload, returning one ArrayBuffer ready to send.
 */
export function buildFrame(slot, sequence, jpeg, width, height, {
  timestampMs = Date.now(),
  flags = FRAME_FLAG_NONE,
} = {}) {
  const buffer = new ArrayBuffer(FRAME_HEADER_SIZE + jpeg.length);
  const view = new DataView(buffer);
  const bytes = new Uint8Array(buffer);

  bytes[0] = 0x56; // V
  bytes[1] = 0x4D; // M
  bytes[2] = 0x46; // F
  bytes[3] = 0x31; // 1

  view.setUint8(4, FRAME_PAYLOAD_JPEG);
  view.setUint8(5, flags);
  view.setUint16(6, slot & 0xFFFF, true);
  view.setUint32(8, sequence >>> 0, true);
  view.setBigInt64(12, BigInt(Math.trunc(timestampMs)), true);
  view.setUint16(20, Math.min(Math.max(width, 0), 0xFFFF), true);
  view.setUint16(22, Math.min(Math.max(height, 0), 0xFFFF), true);

  bytes.set(jpeg, FRAME_HEADER_SIZE);
  return buffer;
}
