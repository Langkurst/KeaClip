// SPDX-License-Identifier: GPL-3.0-or-later. Part of KeaClip; this unminified file is its own source.
// Playback engines. MseEngine streams the fragmented MP4 through Media Source Extensions, fetching only the fragments
// around the playhead (and only the encrypted records that hold them). BlobEngine is the fallback for browsers without
// MSE support for the codecs: it downloads and decrypts the whole file, then plays it from memory.

const MB = 1024 * 1024;

function once(target, type) {
  return new Promise((resolve, reject) => {
    const done = event => { target.removeEventListener(type, done); target.removeEventListener("error", fail); resolve(event); };
    const fail = () => { target.removeEventListener(type, done); target.removeEventListener("error", fail); reject(new Error(`${type} failed`)); };
    target.addEventListener(type, done);
    if (type !== "error") target.addEventListener("error", fail);
  });
}

export function mediaSourceClass() {
  return globalThis.ManagedMediaSource || globalThis.MediaSource || null;
}

/** True when this browser can stream the file with MSE. */
export function canStream(meta) {
  const MS = mediaSourceClass();
  try { return !!MS && MS.isTypeSupported(meta.mime); } catch { return false; }
}

export class MseEngine {
  constructor(video, media, meta, { onError = () => {}, aheadSeconds = 30, behindSeconds = 30, maxAheadBytes = 48 * MB } = {}) {
    this.video = video; this.media = media; this.meta = meta; this.onError = onError;
    this.ahead = aheadSeconds; this.behind = behindSeconds; this.maxAheadBytes = maxAheadBytes;
    this.frags = meta.frags;
    this.next = 0;            // The next fragment to append when playing straight on.
    this.generation = 0;      // Bumped by a seek outside the buffer, so stale fetches are dropped.
    this.running = false; this.again = false; this.started = false; this.ended = false;
    this.controller = null;
  }

  async start() {
    const MS = mediaSourceClass();
    this.source = new MS();
    // Safari's ManagedMediaSource only opens when remote playback (AirPlay) is turned off or given an alternative.
    if (globalThis.ManagedMediaSource && MS === globalThis.ManagedMediaSource) this.video.disableRemotePlayback = true;
    const opened = once(this.source, "sourceopen");
    this.objectUrl = URL.createObjectURL(this.source);
    this.video.src = this.objectUrl;
    await opened;
    this.source.duration = this.meta.duration;
    this.buffer = this.source.addSourceBuffer(this.meta.mime);
    this.buffer.mode = "segments";
    await this.append(await this.media.read(0, this.meta.init));
    const tick = () => this.pump();
    for (const type of ["timeupdate", "waiting", "playing", "progress"]) this.video.addEventListener(type, tick);
    this.video.addEventListener("seeking", () => this.onSeeking());
    this.video.addEventListener("waiting", () => this.jumpGap());
    this.source.addEventListener("startstreaming", tick);
    this.pump();
  }

  fragmentEnd(index) { return index + 1 < this.frags.length ? this.frags[index + 1][1] : this.meta.end; }

  /** The last fragment whose start time is at or before t. */
  fragmentAt(t) {
    let low = 0, high = this.frags.length - 1;
    while (low < high) {
      const middle = (low + high + 1) >> 1;
      if (this.frags[middle][0] <= t + 0.01) low = middle; else high = middle - 1;
    }
    return low;
  }

  /** The end of the buffered range that contains t, or null when t is not buffered. */
  bufferedEnd(t) {
    const ranges = this.buffer ? this.buffer.buffered : this.video.buffered;
    for (let i = 0; i < ranges.length; i++) if (ranges.start(i) - 0.3 <= t && t < ranges.end(i) - 0.05) return ranges.end(i);
    return null;
  }

  onSeeking() {
    const t = this.video.currentTime;
    const end = this.bufferedEnd(t);
    const index = this.fragmentAt(end ?? t);
    // Seeking inside what is buffered and continuous with the planned fetch: nothing to change.
    if (end !== null && index >= this.next - 1 && index <= this.next) { this.pump(); return; }
    this.generation++;
    if (this.controller) this.controller.abort();
    this.next = index;
    this.ended = false;
    this.pump();
  }

  pump() {
    if (this.running) { this.again = true; return; }
    this.loop();
  }

  async loop() {
    this.running = true;
    try {
      do {
        this.again = false;
        try { await this.fill(); }
        catch (error) {
          if (error && error.name === "AbortError") { this.again = true; continue; }
          this.onError(error);
          return;
        }
      } while (this.again);
    } finally { this.running = false; }
  }

  async fill() {
    const frags = this.frags, count = frags.length;
    for (;;) {
      if (!this.source || this.source.readyState === "closed") return;
      const t = this.video.currentTime;
      if (this.next >= count) { this.finish(); return; }
      const here = this.fragmentAt(t);
      if (frags[this.next][0] > t + this.ahead || frags[this.next][1] - frags[here][1] > this.maxAheadBytes) return;
      // Whole fragments, about 1 MB for a quick start and then up to 4 MB per request.
      const limit = this.started ? 4 * MB : 1 * MB;
      let last = this.next, bytes = this.fragmentEnd(last) - frags[last][1];
      while (last + 1 < count && frags[last + 1][0] <= t + this.ahead) {
        const size = this.fragmentEnd(last + 1) - frags[last + 1][1];
        if (bytes + size > limit) break;
        last++; bytes += size;
      }
      const generation = this.generation;
      const controller = new AbortController();
      this.controller = controller;
      const data = await this.media.read(frags[this.next][1], this.fragmentEnd(last), controller.signal);
      if (generation !== this.generation) continue;
      await this.makeRoom(t, false);
      try {
        await this.append(data);
      } catch (error) {
        if (error && error.name === "QuotaExceededError") {
          // The browser's buffer is full: drop more of what was already played and try again shortly.
          await this.makeRoom(this.video.currentTime, true);
          setTimeout(() => this.pump(), 1000);
          return;
        }
        throw error;
      }
      this.started = true;
      if (generation !== this.generation) continue;
      this.next = last + 1;
      this.jumpGap();
    }
  }

  async append(bytes) {
    await this.idle();
    if (this.source.readyState === "closed") throw new Error("The browser stopped the media stream.");
    this.ended = false;
    this.buffer.appendBuffer(bytes);
    await this.idle();
    if (this.source.readyState === "closed" || this.video.error) throw new Error("The browser could not decode this video.");
  }

  idle() { return this.buffer.updating ? once(this.buffer, "updateend") : Promise.resolve(); }

  async remove(start, end) {
    if (end - start < 0.5) return;
    await this.idle();
    this.buffer.remove(start, end);
    await this.idle();
  }

  // Frees memory: drops what is well behind the playhead and stale ranges far ahead of it (left over from an earlier seek).
  async makeRoom(t, aggressive) {
    const behind = aggressive ? 5 : this.behind;
    const ranges = [];
    for (let i = 0; i < this.buffer.buffered.length; i++) ranges.push([this.buffer.buffered.start(i), this.buffer.buffered.end(i)]);
    for (const [start, end] of ranges) {
      if (end < t - behind) await this.remove(start, end);
      else if (start < t - behind) await this.remove(start, t - behind);
      else if (start > t + this.ahead + 30) await this.remove(start, end);
    }
  }

  // Video often starts a few milliseconds after zero (B-frame delay), and appends after a seek can leave a tiny gap.
  jumpGap() {
    const video = this.video, t = video.currentTime;
    if (video.readyState >= 3 || this.bufferedEnd(t) !== null) return;
    const ranges = this.buffer.buffered;
    for (let i = 0; i < ranges.length; i++) {
      const start = ranges.start(i);
      if (start > t && start - t < 1.0) { video.currentTime = start + 0.001; return; }
    }
  }

  finish() {
    if (this.ended || this.buffer.updating || this.source.readyState !== "open") return;
    // Only end the stream when everything up to the end is buffered (after a seek the early part may not be).
    if (this.bufferedEnd(this.video.currentTime) === null && this.video.currentTime < this.meta.duration - 0.5) return;
    this.ended = true;
    try { this.source.endOfStream(); } catch { /* A new append reopens the stream. */ }
  }

  stats() { return { requests: this.media.requests, fetchedBytes: this.media.fetchedBytes, next: this.next, engine: "mse" }; }
}

export class BlobEngine {
  constructor(video, media, meta) { this.video = video; this.media = media; this.meta = meta; this.blob = null; }

  async start(onProgress = () => {}) {
    const size = this.meta.size, step = 4 * MB, parts = [];
    for (let offset = 0; offset < size; offset += step) {
      parts.push(await this.media.read(offset, Math.min(size, offset + step)));
      onProgress(Math.min(1, (offset + step) / size));
    }
    this.blob = new Blob(parts, { type: this.meta.mime.split(";")[0] });
    this.objectUrl = URL.createObjectURL(this.blob);
    this.video.src = this.objectUrl;
  }

  stats() { return { requests: this.media.requests, fetchedBytes: this.media.fetchedBytes, engine: "blob" }; }
}
