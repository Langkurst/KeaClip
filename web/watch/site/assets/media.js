// SPDX-License-Identifier: GPL-3.0-or-later. Part of KeaClip; this unminified file is its own source.
// Encrypted media over HTTP Range requests: fetches only the records that cover a plaintext range, decrypts them in the
// browser and keeps a small cache, so seeking downloads only what it needs. No DOM access (runs in Node tests too).

import { DecryptError, SeekableLayout, decryptMessage, decryptRecord, deriveKeys, parseHeader } from "./ece.js";

/** Storage answered that the object is gone (expired, deleted or never uploaded). */
export class MissingError extends Error {
  constructor(message) { super(message); this.name = "MissingError"; }
}
/** Storage could not be reached or answered with an error. */
export class NetworkError extends Error {
  constructor(message) { super(message); this.name = "NetworkError"; }
}

const CACHE_RECORDS = 64;

async function request(fetchImpl, url, init) {
  let response;
  try {
    // credentials: "omit" and no-referrer: storage gets nothing but the object path and the byte range.
    response = await fetchImpl(url, { ...init, credentials: "omit", referrerPolicy: "no-referrer", cache: "default" });
  } catch (error) {
    if (error && error.name === "AbortError") throw error;
    throw new NetworkError("Storage could not be reached.");
  }
  if (response.status === 404 || response.status === 403 || response.status === 410) throw new MissingError("This video is no longer available.");
  if (!response.ok) throw new NetworkError(`Storage answered HTTP ${response.status}.`);
  return response;
}

/** Fetches and decrypts the small metadata object (a whole RFC 8188 message). */
export async function loadMetadata(url, secret, { fetchImpl = globalThis.fetch.bind(globalThis), signal } = {}) {
  const response = await request(fetchImpl, url, { signal });
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.length > 8 * 1024 * 1024) throw new DecryptError("The metadata is too large.");
  const plain = await decryptMessage(secret, bytes);
  let meta;
  try { meta = JSON.parse(new TextDecoder().decode(plain)); } catch { throw new DecryptError("The metadata is not readable."); }
  validateMetadata(meta);
  return meta;
}

export function validateMetadata(meta) {
  const ok = meta && meta.v === 1 && typeof meta.name === "string" && (meta.kind === "video" || meta.kind === "audio")
    && typeof meta.mime === "string" && Number.isSafeInteger(meta.size) && meta.size > 0 && Number.isSafeInteger(meta.end) && meta.end <= meta.size
    && Number.isSafeInteger(meta.init) && meta.init > 0 && meta.init < meta.end && typeof meta.duration === "number" && meta.duration > 0
    && Array.isArray(meta.frags) && meta.frags.length > 0
    && meta.frags.every((f, i) => Array.isArray(f) && typeof f[0] === "number" && Number.isSafeInteger(f[1]) && f[1] >= meta.init && f[1] < meta.end
      && (i === 0 || (f[1] > meta.frags[i - 1][1] && f[0] + 0.001 >= meta.frags[i - 1][0])));
  if (!ok) throw new DecryptError("The metadata is not in the expected format.");
}

/** A seekable encrypted object: read(start, end) returns plaintext bytes [start, end). */
export class EncryptedMedia {
  static async open(url, secret, { fetchImpl = globalThis.fetch.bind(globalThis), signal, expectedSize } = {}) {
    // The first request reads the header and the first record together (records are at most 64 KiB in KeaClip's layout).
    const response = await request(fetchImpl, url, { headers: { Range: "bytes=0-65556" }, signal });
    const first = new Uint8Array(await response.arrayBuffer());
    let total;
    if (response.status === 206) {
      const match = /\/(\d+)\s*$/.exec(response.headers.get("Content-Range") || "");
      if (!match) throw new NetworkError("Storage did not report the object size (check the CORS rule exposes Content-Range).");
      total = Number(match[1]);
    } else {
      total = first.length; // Storage ignored the range and sent the whole object.
    }
    const header = parseHeader(first);
    const layout = new SeekableLayout(header, total);
    if (expectedSize !== undefined && layout.plainLength !== expectedSize) throw new DecryptError("The media size does not match its metadata.");
    const keys = await deriveKeys(secret, header.salt);
    const media = new EncryptedMedia(url, fetchImpl, layout, keys);
    // Keep the records that arrived with the header.
    await media.#ingest(0, first.subarray(header.headerLength, Math.min(first.length, layout.cipherRange(0, 0)[1])), 0, 0, response.status !== 206 ? first : null);
    return media;
  }

  #url; #fetch; #keys; #cache = new Map(); #whole = null;
  /** Bytes fetched from storage so far (for diagnostics and tests). */
  fetchedBytes = 0;
  requests = 0;

  constructor(url, fetchImpl, layout, keys) {
    this.#url = url; this.#fetch = fetchImpl; this.layout = layout; this.#keys = keys;
  }

  get size() { return this.layout.plainLength; }

  /** Plaintext bytes [start, end). Only missing records are fetched, with one Range request for the missing span. */
  async read(start, end, signal) {
    const [first, last] = this.layout.recordsFor(start, end);
    let missingFirst = -1, missingLast = -1;
    for (let i = first; i <= last; i++) if (!this.#cache.has(i)) { if (missingFirst < 0) missingFirst = i; missingLast = i; }
    if (missingFirst >= 0) {
      const [from, to] = this.layout.cipherRange(missingFirst, missingLast);
      let bytes;
      if (this.#whole) bytes = this.#whole.subarray(from, to);
      else {
        this.requests++;
        const response = await request(this.#fetch, this.#url, { headers: { Range: `bytes=${from}-${to - 1}` }, signal });
        const body = new Uint8Array(await response.arrayBuffer());
        bytes = response.status === 206 ? body : body.subarray(from, to);
        this.fetchedBytes += body.length;
        if (bytes.length !== to - from) throw new NetworkError("Storage returned a different byte range than requested.");
      }
      await this.#ingest(missingFirst, bytes, missingFirst, missingLast, null);
    }
    const out = new Uint8Array(end - start);
    const payload = this.layout.payload;
    for (let i = first; i <= last; i++) {
      const record = this.#cache.get(i);
      if (!record) throw new DecryptError("A record is missing.");
      // Refresh its place in the least-recently-used order.
      this.#cache.delete(i); this.#cache.set(i, record);
      const recordStart = i * payload;
      const from = Math.max(start, recordStart), to = Math.min(end, recordStart + record.length);
      out.set(record.subarray(from - recordStart, to - recordStart), from - start);
    }
    // Evict only after copying, so a read larger than the cache still finds every record it needs.
    while (this.#cache.size > CACHE_RECORDS) this.#cache.delete(this.#cache.keys().next().value);
    return out;
  }

  // Decrypts consecutive records (in parallel; WebCrypto runs them off the main thread) and caches them.
  async #ingest(firstIndex, bytes, first, last, whole) {
    if (whole) this.#whole = whole;
    const { rs, records } = this.layout;
    const count = Math.min(Math.ceil(bytes.length / rs), last - first + 1 || 1);
    const jobs = [];
    for (let k = 0; k < count; k++) {
      const index = firstIndex + k;
      const cipher = bytes.subarray(k * rs, Math.min((k + 1) * rs, bytes.length));
      const final = index === records - 1;
      // A partial first read (header request) may hold only part of record 0 when the record size is larger; skip it.
      if (!final && cipher.length !== rs) continue;
      jobs.push(decryptRecord(this.#keys, index, cipher, final, true, rs).then(plain => [index, plain]));
    }
    for (const [index, plain] of await Promise.all(jobs)) this.#cache.set(index, plain);
  }
}

export { DecryptError };
