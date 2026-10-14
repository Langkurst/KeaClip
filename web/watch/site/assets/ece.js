// SPDX-License-Identifier: GPL-3.0-or-later. Part of KeaClip; this unminified file is its own source.
// KeaClip watch links: RFC 8188 (aes128gcm) decryption with WebCrypto, plus the seekable record layout.
// No DOM access, so the same module runs in browsers and in Node's test runner. The key never leaves this page.

const subtle = globalThis.crypto.subtle;
const encoder = new TextEncoder();
const KEY_INFO = encoder.encode("Content-Encoding: aes128gcm\0");
const NONCE_INFO = encoder.encode("Content-Encoding: nonce\0");
export const HEADER_LENGTH = 21;
export const TAG_LENGTH = 16;
export const RECORD_OVERHEAD = 17;

/** Thrown when the key does not open the data, or the data was changed or cut short. */
export class DecryptError extends Error {
  constructor(message) { super(message); this.name = "DecryptError"; }
}

/** Thrown when a link fragment is not `#<id>.<key>`. */
export class LinkError extends Error {
  constructor(message) { super(message); this.name = "LinkError"; }
}

const BASE64URL = /^[A-Za-z0-9_-]*$/;

export function base64UrlDecode(text) {
  if (!BASE64URL.test(text) || text.length % 4 === 1) throw new LinkError("Not base64url.");
  const padded = text.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((text.length + 3) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/** Parses a link fragment (with or without '#') into the object id and the 16-byte key. */
export function parseFragment(fragment) {
  const value = String(fragment || "").replace(/^#/, "");
  const match = /^([A-Za-z0-9_-]{22})\.([A-Za-z0-9_-]{22})$/.exec(value);
  if (!match) throw new LinkError("The link is incomplete.");
  const key = base64UrlDecode(match[2]);
  if (key.length !== 16) throw new LinkError("The link key has the wrong length.");
  return { id: match[1], key };
}

/** Reads the 21-byte RFC 8188 header (plus any key id). */
export function parseHeader(bytes) {
  if (bytes.length < HEADER_LENGTH) throw new DecryptError("The encrypted header is incomplete.");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const rs = view.getUint32(16);
  const idLength = bytes[20];
  if (rs < 18 || rs > 16777216) throw new DecryptError("The record size is invalid.");
  if (bytes.length < HEADER_LENGTH + idLength) throw new DecryptError("The encrypted header is incomplete.");
  return { salt: bytes.slice(0, 16), rs, headerLength: HEADER_LENGTH + idLength };
}

/** Derives the AES-128-GCM content key and the 12-byte base nonce from the secret and the message salt (HKDF-SHA-256). */
export async function deriveKeys(secret, salt) {
  const ikm = await subtle.importKey("raw", secret, "HKDF", false, ["deriveBits"]);
  const keyBits = await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info: KEY_INFO }, ikm, 128);
  const nonceBits = await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info: NONCE_INFO }, ikm, 96);
  const key = await subtle.importKey("raw", keyBits, { name: "AES-GCM" }, false, ["decrypt"]);
  return { key, baseNonce: new Uint8Array(nonceBits) };
}

/** The record nonce: the base nonce with its last 8 bytes XORed with the big-endian record sequence number. */
export function recordNonce(baseNonce, sequence) {
  const nonce = baseNonce.slice();
  const view = new DataView(nonce.buffer);
  const high = Math.floor(sequence / 4294967296), low = sequence >>> 0;
  view.setUint32(4, view.getUint32(4) ^ high);
  view.setUint32(8, view.getUint32(8) ^ low);
  return nonce;
}

/**
 * Decrypts one record. `strict` is the seekable layout: a non-final record must carry exactly rs - 17 bytes (no padding),
 * so plaintext offsets map to records. The final record ends with delimiter 2, every other with 1, so a stream cut at a
 * record boundary is rejected.
 */
export async function decryptRecord(keys, sequence, cipher, final, strict, rs) {
  if (cipher.length <= TAG_LENGTH) throw new DecryptError("An encrypted record is incomplete.");
  let plain;
  try {
    plain = new Uint8Array(await subtle.decrypt({ name: "AES-GCM", iv: recordNonce(keys.baseNonce, sequence), tagLength: 128 }, keys.key, cipher));
  } catch {
    throw new DecryptError("The key does not open this data, or the data was changed.");
  }
  let end = plain.length - 1;
  while (end >= 0 && plain[end] === 0) end--;
  if (end < 0 || plain[end] !== (final ? 2 : 1)) throw new DecryptError("An encrypted record has the wrong delimiter (the data may be cut short).");
  if (strict && !final && (cipher.length !== rs || end !== rs - RECORD_OVERHEAD)) throw new DecryptError("An encrypted record has padding where the seekable layout allows none.");
  return plain.subarray(0, end);
}

/** Decrypts a whole RFC 8188 message held in memory (any valid padding is accepted). */
export async function decryptMessage(secret, bytes) {
  const header = parseHeader(bytes);
  const keys = await deriveKeys(secret, header.salt);
  const parts = [];
  let offset = header.headerLength, sequence = 0, total = 0;
  if (offset >= bytes.length) throw new DecryptError("The encrypted message has no records.");
  while (offset < bytes.length) {
    const end = Math.min(offset + header.rs, bytes.length);
    const part = await decryptRecord(keys, sequence++, bytes.subarray(offset, end), end === bytes.length, false, header.rs);
    parts.push(part); total += part.length; offset = end;
  }
  const out = new Uint8Array(total);
  let position = 0;
  for (const part of parts) { out.set(part, position); position += part.length; }
  return out;
}

/** Plaintext-to-ciphertext arithmetic for the seekable layout (every non-final record holds rs - 17 bytes). */
export class SeekableLayout {
  constructor(header, cipherLength) {
    this.rs = header.rs;
    this.headerLength = header.headerLength;
    this.payload = header.rs - RECORD_OVERHEAD;
    this.cipherLength = cipherLength;
    const body = cipherLength - header.headerLength;
    if (body < RECORD_OVERHEAD) throw new DecryptError("The encrypted data is too short.");
    this.records = Math.ceil(body / header.rs);
    const lastCipher = body - (this.records - 1) * header.rs;
    if (lastCipher < RECORD_OVERHEAD) throw new DecryptError("The encrypted data was cut short.");
    this.plainLength = (this.records - 1) * this.payload + (lastCipher - RECORD_OVERHEAD);
  }

  /** The [first, last] record indexes covering plaintext bytes [start, end). */
  recordsFor(start, end) {
    if (!(start >= 0 && end > start && end <= this.plainLength)) throw new RangeError("The range is outside the media.");
    return [Math.floor(start / this.payload), Math.floor((end - 1) / this.payload)];
  }

  /** The ciphertext byte range [offset, end) of records first..last. */
  cipherRange(first, last) {
    return [this.headerLength + first * this.rs, Math.min(this.headerLength + (last + 1) * this.rs, this.cipherLength)];
  }
}
