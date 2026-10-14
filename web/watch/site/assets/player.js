// SPDX-License-Identifier: GPL-3.0-or-later. Part of KeaClip; this unminified file is its own source.
// The KeaClip watch page: reads #<id>.<key> from the address, decrypts the metadata and streams the video. The fragment is
// never sent anywhere: storage requests carry only the object path and a byte range.

import config from "./config.js";
import { DecryptError, LinkError, parseFragment } from "./ece.js";
import { EncryptedMedia, MissingError, NetworkError, loadMetadata } from "./media.js";
import { BlobEngine, MseEngine, canStream } from "./engine.js";

const $ = id => document.getElementById(id);
const video = $("video"), player = $("player"), seek = $("seek"), played = $("seek-played"), thumb = $("seek-thumb"), bufferedLayer = $("seek-buffered");
const tooltip = $("seek-tip"), timeNow = $("time-now"), timeTotal = $("time-total"), playButton = $("play"), bigPlay = $("big-play");
const muteButton = $("mute"), volume = $("volume"), fullButton = $("fullscreen"), downloadButton = $("download"), stateLayer = $("state");
const root = document.documentElement;

let meta = null, media = null, engine = null, dragging = false, hideTimer = 0, lastTap = 0;

function setState(state) { root.dataset.state = state; }

function formatTime(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) seconds = 0;
  const s = Math.floor(seconds % 60), m = Math.floor(seconds / 60) % 60, h = Math.floor(seconds / 3600);
  return (h > 0 ? `${h}:${String(m).padStart(2, "0")}` : `${m}`) + `:${String(s).padStart(2, "0")}`;
}

function formatBytes(bytes) {
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(2)} GB`;
  if (bytes >= 1e6) return `${(bytes / 1e6).toFixed(bytes >= 1e8 ? 0 : 1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1e3))} KB`;
}

function showError(title, message, retry = false) {
  setState("error");
  stateLayer.hidden = false;
  stateLayer.className = "state error";
  $("state-title").textContent = title;
  $("state-message").textContent = message;
  $("state-retry").hidden = !retry;
  player.setAttribute("aria-busy", "false");
  // Without metadata there is nothing to describe or download; drop the placeholders.
  if (!meta) $("details").hidden = true;
}

function showLoading(message) {
  stateLayer.hidden = false;
  stateLayer.className = "state loading";
  $("state-title").textContent = message;
  $("state-message").textContent = "";
  $("state-retry").hidden = true;
}

function hideState() { stateLayer.hidden = true; player.setAttribute("aria-busy", "false"); }

function explain(error) {
  if (error instanceof LinkError) return ["This link is incomplete", "Copy the whole link, including everything after the # sign, and open it again."];
  if (error instanceof DecryptError) return ["This link's key doesn't match", "The part after # was changed or cut short, so the video can't be unlocked. Ask the sender for the link again."];
  if (error instanceof MissingError) return ["This video is no longer available", "Watch links expire after a while. Ask the sender to share it again."];
  if (error instanceof NetworkError) return ["Couldn't reach the video", "Check your connection and try again."];
  return ["This video can't play here", "Your browser couldn't play it. Try the Download button, or another browser."];
}

// ----- Start-up -----

async function start() {
  setState("loading");
  let link;
  try { link = parseFragment(location.hash); } catch (error) { showError(...explain(error)); return; }
  const base = String(config.mediaBase || "").replace(/\/+$/, "");
  showLoading("Unlocking the video…");
  try {
    meta = await loadMetadata(`${base}/w/${link.id}/meta`, link.key);
    renderDetails();
    media = await EncryptedMedia.open(`${base}/w/${link.id}/media`, link.key, { expectedSize: meta.size });
  } catch (error) {
    const [title, message] = explain(error);
    showError(title, message, error instanceof NetworkError);
    return;
  } finally {
    link.key.fill(0); // The page keeps only non-extractable CryptoKeys from here on.
  }
  downloadButton.disabled = false;
  const forceBlob = new URLSearchParams(location.search).get("engine") === "blob";
  const mobile = matchMedia("(pointer: coarse)").matches;
  const blobLimit = mobile ? (config.mobileBlobLimitBytes || 200e6) : (config.blobLimitBytes || 600e6);
  try {
    if (!forceBlob && canStream(meta)) {
      engine = new MseEngine(video, media, meta, { onError: error => showError(...explain(error)) });
      await engine.start();
    } else if (meta.size <= blobLimit && video.canPlayType(meta.mime.split(";")[0]) !== "") {
      showLoading("Loading the video…");
      engine = new BlobEngine(video, media, meta);
      await engine.start(fraction => { $("state-title").textContent = `Loading the video… ${Math.round(fraction * 100)}%`; });
    } else {
      showError("This browser can't stream this video", "Use the Download button to save it and watch it on this device.");
      return;
    }
  } catch (error) {
    showError(...explain(error), error instanceof NetworkError);
    return;
  }
  hideState();
  setState("ready");
  player.classList.add("ready");
  updateTime();
}

function renderDetails() {
  const title = meta.name.replace(/\.[^.]+$/, "");
  document.title = `${title} · KeaClip`;
  $("title").textContent = title;
  $("title").classList.remove("skeleton");
  const parts = [formatTime(meta.duration)];
  if (meta.kind === "video" && meta.width && meta.height) parts.push(`${meta.width}×${meta.height}`);
  else parts.push("Audio");
  parts.push(formatBytes(meta.size));
  if (meta.expires) {
    const until = new Date(meta.expires);
    if (!Number.isNaN(until.getTime())) parts.push(`Available until about ${until.toLocaleDateString(undefined, { day: "numeric", month: "short" })}`);
  }
  $("facts").textContent = parts.join(" · ");
  $("facts").classList.remove("skeleton");
  timeTotal.textContent = formatTime(meta.duration);
  player.classList.toggle("audio", meta.kind === "audio");
  if (meta.kind === "audio") $("audio-title").textContent = title;
  seek.setAttribute("aria-valuemax", String(Math.round(meta.duration)));
}

// ----- Controls -----

function duration() { return meta ? meta.duration : (video.duration || 0); }

function updateTime() {
  const total = duration();
  const current = dragging ? Number(seek.dataset.preview || 0) : video.currentTime;
  const fraction = total > 0 ? Math.min(1, current / total) : 0;
  played.style.transform = `scaleX(${fraction})`;
  thumb.style.left = `${fraction * 100}%`;
  timeNow.textContent = formatTime(current);
  seek.setAttribute("aria-valuenow", String(Math.round(current)));
  seek.setAttribute("aria-valuetext", `${formatTime(current)} of ${formatTime(total)}`);
}

function updateBuffered() {
  const total = duration();
  if (!(total > 0)) return;
  const ranges = video.buffered;
  while (bufferedLayer.children.length < ranges.length) bufferedLayer.appendChild(document.createElement("span"));
  while (bufferedLayer.children.length > ranges.length) bufferedLayer.lastChild.remove();
  for (let i = 0; i < ranges.length; i++) {
    const span = bufferedLayer.children[i];
    span.style.left = `${(ranges.start(i) / total) * 100}%`;
    span.style.width = `${Math.max(0, (ranges.end(i) - ranges.start(i)) / total) * 100}%`;
  }
}

function setPlaying() {
  const paused = video.paused;
  player.classList.toggle("paused", paused);
  playButton.setAttribute("aria-label", paused ? "Play (k)" : "Pause (k)");
  playButton.title = paused ? "Play (k)" : "Pause (k)";
  if (paused) showControls(); else scheduleHide();
}

async function togglePlay() {
  if (!engine) return;
  if (video.paused || video.ended) {
    try { await video.play(); } catch { /* Autoplay rules: the user can press play again. */ }
  } else video.pause();
}

function seekTo(seconds) {
  if (!engine) return;
  video.currentTime = Math.max(0, Math.min(duration() - 0.05, seconds));
  updateTime();
}

function setVolume(value, muted = value === 0) {
  video.volume = Math.max(0, Math.min(1, value));
  video.muted = muted;
  try { localStorage.setItem("keaclip.volume", JSON.stringify({ volume: video.volume, muted })); } catch { /* Private mode. */ }
}

function updateVolume() {
  const level = video.muted ? 0 : video.volume;
  volume.value = String(Math.round(level * 100));
  volume.style.setProperty("--level", `${level * 100}%`);
  player.dataset.volume = level === 0 ? "mute" : level < 0.5 ? "low" : "high";
  muteButton.setAttribute("aria-label", level === 0 ? "Unmute (m)" : "Mute (m)");
  muteButton.title = level === 0 ? "Unmute (m)" : "Mute (m)";
}

function fullscreenElement() { return document.fullscreenElement || document.webkitFullscreenElement; }

async function toggleFullscreen() {
  try {
    if (fullscreenElement()) await (document.exitFullscreen || document.webkitExitFullscreen).call(document);
    else if (player.requestFullscreen) await player.requestFullscreen({ navigationUI: "hide" });
    else if (player.webkitRequestFullscreen) player.webkitRequestFullscreen();
    else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen(); // iPhone: the system player.
  } catch { /* Not allowed here. */ }
}

function showControls() {
  player.classList.remove("idle");
  clearTimeout(hideTimer);
}

function scheduleHide() {
  clearTimeout(hideTimer);
  if (video.paused || dragging) return;
  hideTimer = setTimeout(() => { if (!video.paused && !dragging && !$("controls").contains(document.activeElement)) player.classList.add("idle"); }, 2600);
}

function fractionAt(event) {
  const rect = seek.getBoundingClientRect();
  return Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
}

function previewAt(event) {
  const fraction = fractionAt(event), time = fraction * duration();
  tooltip.textContent = formatTime(time);
  const rect = seek.getBoundingClientRect(), half = tooltip.offsetWidth / 2;
  tooltip.style.left = `${Math.max(half, Math.min(rect.width - half, fraction * rect.width))}px`;
  return time;
}

seek.addEventListener("pointermove", event => { if (engine) { previewAt(event); seek.classList.add("hover"); if (dragging) { seek.dataset.preview = String(previewAt(event)); updateTime(); } } });
seek.addEventListener("pointerleave", () => { if (!dragging) seek.classList.remove("hover"); });
seek.addEventListener("pointerdown", event => {
  if (!engine || event.button !== 0) return;
  dragging = true;
  seek.setPointerCapture(event.pointerId);
  seek.classList.add("dragging", "hover");
  seek.dataset.preview = String(previewAt(event));
  updateTime();
  showControls();
});
const endDrag = event => {
  if (!dragging) return;
  dragging = false;
  seek.classList.remove("dragging");
  if (event.type === "pointerup") seekTo(Number(seek.dataset.preview || 0));
  if (event.pointerType !== "mouse") seek.classList.remove("hover");
  scheduleHide();
};
seek.addEventListener("pointerup", endDrag);
seek.addEventListener("pointercancel", endDrag);

playButton.addEventListener("click", togglePlay);
bigPlay.addEventListener("click", togglePlay);
muteButton.addEventListener("click", () => setVolume(video.muted || video.volume === 0 ? Math.max(video.volume, 0.5) : video.volume, !(video.muted || video.volume === 0)));
volume.addEventListener("input", () => setVolume(Number(volume.value) / 100));
fullButton.addEventListener("click", toggleFullscreen);
$("state-retry").addEventListener("click", () => location.reload());
$("help").addEventListener("click", () => { $("shortcuts").hidden = !$("shortcuts").hidden; });
$("shortcuts-close").addEventListener("click", () => { $("shortcuts").hidden = true; player.focus(); });

// A click toggles play. On touch screens a tap shows or hides the controls (play with the centre button), and a double tap
// on the left or right third jumps 10 s.
$("surface").addEventListener("click", event => {
  if (!engine) return;
  if (matchMedia("(pointer: coarse)").matches) {
    const rect = $("surface").getBoundingClientRect(), x = (event.clientX - rect.left) / rect.width, now = performance.now();
    if (now - lastTap < 320 && (x < 0.34 || x > 0.66)) { lastTap = 0; seekTo(video.currentTime + (x < 0.5 ? -10 : 10)); showControls(); scheduleHide(); return; }
    lastTap = now;
    if (player.classList.contains("idle") || video.paused) { showControls(); scheduleHide(); }
    else { clearTimeout(hideTimer); player.classList.add("idle"); }
    return;
  }
  togglePlay();
});
$("surface").addEventListener("dblclick", event => { if (!matchMedia("(pointer: coarse)").matches) { event.preventDefault(); toggleFullscreen(); } });
player.addEventListener("pointermove", event => { if (event.pointerType === "mouse") { showControls(); scheduleHide(); } });

video.addEventListener("play", setPlaying);
video.addEventListener("pause", setPlaying);
video.addEventListener("ended", setPlaying);
video.addEventListener("playing", () => { player.classList.add("started"); player.classList.remove("waiting"); setState("playing"); });
video.addEventListener("waiting", () => player.classList.add("waiting"));
video.addEventListener("seeked", () => player.classList.remove("waiting"));
video.addEventListener("canplay", () => player.classList.remove("waiting"));
video.addEventListener("timeupdate", updateTime);
video.addEventListener("progress", updateBuffered);
video.addEventListener("timeupdate", updateBuffered);
video.addEventListener("volumechange", updateVolume);
video.addEventListener("error", () => { if (engine) showError(...explain(new Error("decode"))); });
document.addEventListener("fullscreenchange", () => player.classList.toggle("fullscreen", !!fullscreenElement()));
document.addEventListener("webkitfullscreenchange", () => player.classList.toggle("fullscreen", !!fullscreenElement()));
addEventListener("hashchange", () => location.reload());

document.addEventListener("keydown", event => {
  if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
  const target = event.target;
  if (target === volume && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return;
  if (target instanceof HTMLElement && target.closest("button") && (event.key === " " || event.key === "Enter")) return;
  const total = duration();
  let handled = true;
  switch (event.key) {
    case " ": case "k": case "K": togglePlay(); break;
    case "ArrowLeft": seekTo(video.currentTime - 5); break;
    case "ArrowRight": seekTo(video.currentTime + 5); break;
    case "j": case "J": seekTo(video.currentTime - 10); break;
    case "l": case "L": seekTo(video.currentTime + 10); break;
    case "ArrowUp": setVolume(video.volume + 0.05, false); break;
    case "ArrowDown": setVolume(video.volume - 0.05); break;
    case "m": case "M": muteButton.click(); break;
    case "f": case "F": toggleFullscreen(); break;
    case "Home": seekTo(0); break;
    case "End": seekTo(total); break;
    case "?": $("shortcuts").hidden = !$("shortcuts").hidden; break;
    case "Escape": if (!$("shortcuts").hidden) $("shortcuts").hidden = true; else handled = false; break;
    default:
      if (/^[0-9]$/.test(event.key)) seekTo(total * Number(event.key) / 10); else handled = false;
  }
  if (handled) { event.preventDefault(); showControls(); scheduleHide(); }
});

// ----- Download -----

downloadButton.addEventListener("click", async () => {
  if (!media || downloadButton.classList.contains("busy")) return;
  const label = $("download-label"), original = label.textContent;
  const type = meta.mime.split(";")[0];
  downloadButton.classList.add("busy");
  try {
    if (engine instanceof BlobEngine && engine.blob) { saveBlob(engine.blob); return; }
    let writable = null;
    if (globalThis.showSaveFilePicker && meta.size > 200e6) {
      try {
        const handle = await showSaveFilePicker({ suggestedName: meta.name, types: [{ description: "Video", accept: { [type]: [meta.name.slice(meta.name.lastIndexOf("."))] } }] });
        writable = await handle.createWritable();
      } catch (error) { if (error && error.name === "AbortError") return; }
    }
    const parts = [], step = 4 * 1024 * 1024;
    for (let offset = 0; offset < meta.size; offset += step) {
      const chunk = await media.read(offset, Math.min(meta.size, offset + step));
      if (writable) await writable.write(chunk); else parts.push(chunk);
      label.textContent = `Saving… ${Math.round(Math.min(1, (offset + step) / meta.size) * 100)}%`;
    }
    if (writable) await writable.close(); else saveBlob(new Blob(parts, { type }));
  } catch (error) {
    const [title] = explain(error);
    label.textContent = title;
    setTimeout(() => { label.textContent = original; }, 4000);
    return;
  } finally { downloadButton.classList.remove("busy"); }
  label.textContent = original;
});

function saveBlob(blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url; link.download = meta.name; link.rel = "noopener";
  document.body.appendChild(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

// Restore the viewer's volume; read-only diagnostics for checks (no key or link data).
try {
  const saved = JSON.parse(localStorage.getItem("keaclip.volume") || "null");
  if (saved && typeof saved.volume === "number") { video.volume = Math.max(0, Math.min(1, saved.volume)); video.muted = !!saved.muted; }
} catch { /* Storage unavailable. */ }
updateVolume();
globalThis.keaclipPlayer = Object.freeze({ stats: () => (engine ? engine.stats() : null), state: () => root.dataset.state });

start();
