/*
  A small QR code encoder: byte mode, error correction level M, versions 1 to 15.

  Written rather than pulled from a package because the dashboard has to work on a network with
  no internet access, which is how a sensible surveillance install is wired. The only thing
  VisionMesh encodes is a pairing payload of about a hundred characters, so the scope is
  deliberately narrow: one mode, one error correction level, and the version range that covers it.

  Level M is the right trade here - it tolerates roughly 15% damage, which is what makes a code
  scannable off a glossy monitor at an angle.
*/

/** Total data codewords available at level M, indexed by version. */
const DATA_CODEWORDS = [0, 16, 28, 44, 64, 86, 108, 124, 154, 182, 216, 254, 290, 334, 365, 415];

/** Error correction codewords per block at level M. */
const EC_PER_BLOCK = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24];

/** Block layout at level M: [group1 count, group1 data size, group2 count, group2 data size]. */
const BLOCKS = [
  null,
  [1, 16, 0, 0], [1, 28, 0, 0], [1, 44, 0, 0], [2, 32, 0, 0], [2, 43, 0, 0],
  [4, 27, 0, 0], [4, 31, 0, 0], [2, 38, 2, 39], [3, 36, 2, 37], [4, 43, 1, 44],
  [1, 50, 4, 51], [6, 36, 2, 37], [8, 37, 1, 38], [4, 40, 5, 41], [5, 41, 5, 42],
];

/** Centre coordinates of alignment patterns, by version. */
const ALIGNMENT = [
  [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42],
  [6, 26, 46], [6, 28, 50], [6, 30, 54], [6, 32, 58], [6, 34, 62], [6, 26, 46, 66], [6, 26, 48, 70],
];

/** Pre-computed 15-bit format information for level M, indexed by mask pattern. */
const FORMAT_INFO = [0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0];

/** Pre-computed 18-bit version information, for versions 7 and above. */
const VERSION_INFO = {
  7: 0x07C94, 8: 0x085BC, 9: 0x09A99, 10: 0x0A4D3, 11: 0x0BBF6,
  12: 0x0C762, 13: 0x0D847, 14: 0x0E60D, 15: 0x0F928,
};

// ---- GF(256) arithmetic for Reed-Solomon -----------------------------------

const EXP = new Uint8Array(512);
const LOG = new Uint8Array(256);

(function buildTables() {
  let x = 1;
  for (let i = 0; i < 255; i++) {
    EXP[i] = x;
    LOG[x] = i;
    // The QR field uses the primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D).
    x <<= 1;
    if (x & 0x100) x ^= 0x11D;
  }
  for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255];
})();

function multiply(a, b) {
  if (a === 0 || b === 0) return 0;
  return EXP[LOG[a] + LOG[b]];
}

/** Generator polynomial for the given number of error correction codewords. */
function generatorPolynomial(degree) {
  let polynomial = [1];
  for (let i = 0; i < degree; i++) {
    const next = new Array(polynomial.length + 1).fill(0);
    for (let j = 0; j < polynomial.length; j++) {
      next[j] ^= polynomial[j];
      next[j + 1] ^= multiply(polynomial[j], EXP[i]);
    }
    polynomial = next;
  }
  return polynomial;
}

function errorCorrection(data, ecCount) {
  const generator = generatorPolynomial(ecCount);
  const remainder = new Array(ecCount).fill(0);

  for (const byte of data) {
    const factor = byte ^ remainder[0];
    remainder.shift();
    remainder.push(0);
    if (factor !== 0) {
      for (let i = 0; i < ecCount; i++) {
        remainder[i] ^= multiply(generator[i + 1], factor);
      }
    }
  }
  return remainder;
}

// ---- encoding --------------------------------------------------------------

function toUtf8Bytes(text) {
  return Array.from(new TextEncoder().encode(text));
}

function chooseVersion(byteLength) {
  for (let version = 1; version <= 15; version++) {
    // Mode indicator is 4 bits; the character count is 8 bits up to version 9 and 16 beyond.
    const countBits = version <= 9 ? 8 : 16;
    const capacityBits = DATA_CODEWORDS[version] * 8;
    if (4 + countBits + byteLength * 8 <= capacityBits) return version;
  }
  throw new Error('That value is too long for a QR code this encoder can produce.');
}

function buildCodewords(bytes, version) {
  const bits = [];
  const push = (value, length) => {
    for (let i = length - 1; i >= 0; i--) bits.push((value >> i) & 1);
  };

  push(0b0100, 4);                                   // byte mode
  push(bytes.length, version <= 9 ? 8 : 16);
  for (const byte of bytes) push(byte, 8);

  const capacityBits = DATA_CODEWORDS[version] * 8;

  // Terminator: up to four zero bits, but never past the end of the capacity.
  push(0, Math.min(4, capacityBits - bits.length));
  while (bits.length % 8 !== 0) bits.push(0);

  const codewords = [];
  for (let i = 0; i < bits.length; i += 8) {
    let byte = 0;
    for (let j = 0; j < 8; j++) byte = (byte << 1) | bits[i + j];
    codewords.push(byte);
  }

  // Alternating pad bytes defined by the standard.
  const padding = [0xEC, 0x11];
  let padIndex = 0;
  while (codewords.length < DATA_CODEWORDS[version]) {
    codewords.push(padding[padIndex++ % 2]);
  }

  return codewords;
}

/** Splits data into blocks, computes error correction, and interleaves as the standard requires. */
function interleave(codewords, version) {
  const [group1Count, group1Size, group2Count, group2Size] = BLOCKS[version];
  const ecCount = EC_PER_BLOCK[version];

  const dataBlocks = [];
  const ecBlocks = [];
  let offset = 0;

  for (let i = 0; i < group1Count; i++) {
    const block = codewords.slice(offset, offset + group1Size);
    offset += group1Size;
    dataBlocks.push(block);
    ecBlocks.push(errorCorrection(block, ecCount));
  }
  for (let i = 0; i < group2Count; i++) {
    const block = codewords.slice(offset, offset + group2Size);
    offset += group2Size;
    dataBlocks.push(block);
    ecBlocks.push(errorCorrection(block, ecCount));
  }

  const result = [];
  const maxDataLength = Math.max(group1Size, group2Size);

  for (let i = 0; i < maxDataLength; i++) {
    for (const block of dataBlocks) {
      if (i < block.length) result.push(block[i]);
    }
  }
  for (let i = 0; i < ecCount; i++) {
    for (const block of ecBlocks) result.push(block[i]);
  }

  return result;
}

// ---- matrix construction ---------------------------------------------------

function createMatrix(version) {
  const size = version * 4 + 17;
  const modules = Array.from({ length: size }, () => new Array(size).fill(null));
  const reserved = Array.from({ length: size }, () => new Array(size).fill(false));

  const setFunction = (x, y, value) => {
    if (x < 0 || y < 0 || x >= size || y >= size) return;
    modules[y][x] = value;
    reserved[y][x] = true;
  };

  // Finder patterns and their separators, at three corners.
  for (const [originX, originY] of [[0, 0], [size - 7, 0], [0, size - 7]]) {
    for (let y = -1; y <= 7; y++) {
      for (let x = -1; x <= 7; x++) {
        const inside = x >= 0 && x <= 6 && y >= 0 && y <= 6;
        const isRing = inside && (x === 0 || x === 6 || y === 0 || y === 6);
        const isCore = inside && x >= 2 && x <= 4 && y >= 2 && y <= 4;
        setFunction(originX + x, originY + y, isRing || isCore ? 1 : 0);
      }
    }
  }

  // Timing patterns.
  for (let i = 8; i < size - 8; i++) {
    setFunction(i, 6, i % 2 === 0 ? 1 : 0);
    setFunction(6, i, i % 2 === 0 ? 1 : 0);
  }

  // Alignment patterns, skipping the three that would overlap a finder.
  const centres = ALIGNMENT[version];
  for (const centreY of centres) {
    for (const centreX of centres) {
      const nearFinder = (centreX <= 8 && centreY <= 8)
                      || (centreX >= size - 9 && centreY <= 8)
                      || (centreX <= 8 && centreY >= size - 9);
      if (nearFinder) continue;

      for (let y = -2; y <= 2; y++) {
        for (let x = -2; x <= 2; x++) {
          const ring = Math.max(Math.abs(x), Math.abs(y));
          setFunction(centreX + x, centreY + y, ring === 1 ? 0 : 1);
        }
      }
    }
  }

  // The dark module, always set, just above the lower-left finder.
  setFunction(8, size - 8, 1);

  // Reserve the format information areas; the values are written after masking.
  for (let i = 0; i < 9; i++) {
    if (!reserved[i][8]) setFunction(8, i, 0);
    if (!reserved[8][i]) setFunction(i, 8, 0);
  }
  for (let i = 0; i < 8; i++) {
    if (!reserved[8][size - 1 - i]) setFunction(size - 1 - i, 8, 0);
    if (!reserved[size - 1 - i][8]) setFunction(8, size - 1 - i, 0);
  }

  // Version information blocks, for version 7 and above.
  if (version >= 7) {
    const info = VERSION_INFO[version];
    for (let i = 0; i < 18; i++) {
      const bit = (info >> i) & 1;
      const x = Math.floor(i / 3);
      const y = size - 11 + (i % 3);
      setFunction(x, y, bit);
      setFunction(y, x, bit);
    }
  }

  return { modules, reserved, size };
}

/** Walks the zigzag data path, skipping function modules. */
function placeData(matrix, codewords) {
  const { modules, reserved, size } = matrix;
  const bits = [];
  for (const codeword of codewords) {
    for (let i = 7; i >= 0; i--) bits.push((codeword >> i) & 1);
  }

  let index = 0;
  let upward = true;

  for (let right = size - 1; right >= 1; right -= 2) {
    // Column 6 is the vertical timing pattern and is skipped entirely.
    if (right === 6) right = 5;

    for (let step = 0; step < size; step++) {
      const y = upward ? size - 1 - step : step;
      for (const x of [right, right - 1]) {
        if (reserved[y][x]) continue;
        modules[y][x] = index < bits.length ? bits[index] : 0;
        index++;
      }
    }
    upward = !upward;
  }
}

function maskCondition(mask, x, y) {
  switch (mask) {
    case 0: return (y + x) % 2 === 0;
    case 1: return y % 2 === 0;
    case 2: return x % 3 === 0;
    case 3: return (y + x) % 3 === 0;
    case 4: return (Math.floor(y / 2) + Math.floor(x / 3)) % 2 === 0;
    case 5: return ((y * x) % 2) + ((y * x) % 3) === 0;
    case 6: return (((y * x) % 2) + ((y * x) % 3)) % 2 === 0;
    case 7: return (((y + x) % 2) + ((y * x) % 3)) % 2 === 0;
    default: return false;
  }
}

function applyMask(matrix, mask) {
  const { modules, reserved, size } = matrix;
  const masked = modules.map((row) => row.slice());

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (reserved[y][x]) continue;
      if (maskCondition(mask, x, y)) masked[y][x] ^= 1;
    }
  }
  return masked;
}

function writeFormatInfo(modules, size, mask) {
  const info = FORMAT_INFO[mask];

  for (let i = 0; i < 15; i++) {
    const bit = (info >> i) & 1;

    // Copy one: around the top-left finder.
    if (i < 6) modules[i][8] = bit;
    else if (i === 6) modules[7][8] = bit;
    else if (i === 7) modules[8][8] = bit;
    else if (i === 8) modules[8][7] = bit;
    else modules[8][14 - i] = bit;

    // Copy two: split between the other two finders.
    if (i < 8) modules[8][size - 1 - i] = bit;
    else modules[size - 15 + i][8] = bit;
  }
}

/** Scores a masked matrix by the standard's four penalty rules. Lower is better. */
function penalty(modules, size) {
  let score = 0;

  // Rule 1: runs of five or more same-coloured modules in a row or column.
  for (let i = 0; i < size; i++) {
    for (const readRow of [true, false]) {
      let runColour = -1;
      let runLength = 0;
      for (let j = 0; j < size; j++) {
        const value = readRow ? modules[i][j] : modules[j][i];
        if (value === runColour) {
          runLength++;
        } else {
          if (runLength >= 5) score += 3 + (runLength - 5);
          runColour = value;
          runLength = 1;
        }
      }
      if (runLength >= 5) score += 3 + (runLength - 5);
    }
  }

  // Rule 2: 2x2 blocks of one colour.
  for (let y = 0; y < size - 1; y++) {
    for (let x = 0; x < size - 1; x++) {
      const value = modules[y][x];
      if (value === modules[y][x + 1] && value === modules[y + 1][x] && value === modules[y + 1][x + 1]) {
        score += 3;
      }
    }
  }

  // Rule 3: patterns that look like a finder, which would confuse a scanner.
  const patternA = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];
  const patternB = [0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];
  for (let i = 0; i < size; i++) {
    for (let j = 0; j <= size - 11; j++) {
      let matchesRowA = true, matchesRowB = true, matchesColumnA = true, matchesColumnB = true;
      for (let k = 0; k < 11; k++) {
        if (modules[i][j + k] !== patternA[k]) matchesRowA = false;
        if (modules[i][j + k] !== patternB[k]) matchesRowB = false;
        if (modules[j + k][i] !== patternA[k]) matchesColumnA = false;
        if (modules[j + k][i] !== patternB[k]) matchesColumnB = false;
      }
      if (matchesRowA) score += 40;
      if (matchesRowB) score += 40;
      if (matchesColumnA) score += 40;
      if (matchesColumnB) score += 40;
    }
  }

  // Rule 4: deviation from an even balance of dark and light.
  let dark = 0;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) dark += modules[y][x];
  }
  const percent = (dark * 100) / (size * size);
  score += Math.floor(Math.abs(percent - 50) / 5) * 10;

  return score;
}

/**
 * Encodes text as a QR matrix.
 * Returns a square array of 0 and 1, where 1 is a dark module.
 */
export function encodeQr(text) {
  const bytes = toUtf8Bytes(text);
  const version = chooseVersion(bytes.length);

  const codewords = interleave(buildCodewords(bytes, version), version);
  const matrix = createMatrix(version);
  placeData(matrix, codewords);

  // Every mask is tried and the least penalised wins, which is what the standard asks for and
  // what makes the result reliably scannable rather than merely valid.
  let best = null;
  let bestScore = Infinity;

  for (let mask = 0; mask < 8; mask++) {
    const candidate = applyMask(matrix, mask);
    writeFormatInfo(candidate, matrix.size, mask);

    const score = penalty(candidate, matrix.size);
    if (score < bestScore) {
      bestScore = score;
      best = candidate;
    }
  }

  return best;
}
