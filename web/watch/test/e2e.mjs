// End-to-end check of the watch page in a real headless browser (Edge or Chrome) against the local test servers.
// Fixtures come from the .NET pipeline (real FFmpeg copy, .NET encryption):
//   set KLIPPI_FFMPEG_DIR and KLIPPI_WATCH_FIXTURES=<folder>, run the WatchLinkRealMediaTests, then
//   node web/watch/test/e2e.mjs <folder> [screenshot folder]
// Proves: decryption and playback start, seeking that fetches only the needed range, keyboard seeking, audio-only and
// converted files, the whole-file fallback, the download, the wrong-key / expired / incomplete-link messages, and that
// neither server ever receives the key (the fragment) in a URL, header or referrer.

import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { startServers } from "./server.mjs";

const fixtures = resolve(process.argv[2] || "artifacts/watch-fixtures");
const shots = process.argv[3] ? resolve(process.argv[3]) : null;
const links = JSON.parse(readFileSync(join(fixtures, "links.json"), "utf8"));
const browserPath = [process.env.KEACLIP_BROWSER, "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "/usr/bin/google-chrome", "/usr/bin/chromium"]
  .find(path => path && existsSync(path));
if (!browserPath) { console.error("No Edge or Chrome found; set KEACLIP_BROWSER."); process.exit(2); }

const results = [];
const check = (name, ok, detail = "") => { results.push({ name, ok, detail }); console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`); };
const sleep = ms => new Promise(done => setTimeout(done, ms));

// ----- A minimal Chrome DevTools Protocol client over the built-in WebSocket -----
async function launch() {
  const profile = mkdtempSync(join(tmpdir(), "keaclip-e2e-"));
  const downloads = join(profile, "downloads");
  mkdirSync(downloads);
  const child = spawn(browserPath, ["--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check",
    "--autoplay-policy=no-user-gesture-required", "--mute-audio", "--disable-extensions", "--disable-background-networking", "about:blank"], { stdio: "ignore" });
  let port;
  for (let i = 0; i < 100 && !port; i++) {
    await sleep(100);
    try { port = readFileSync(join(profile, "DevToolsActivePort"), "utf8").split("\n")[0].trim(); } catch { /* Not yet. */ }
  }
  if (!port) throw new Error("The browser did not start.");
  const version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  const socket = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((done, fail) => { socket.onopen = done; socket.onerror = fail; });
  let id = 0;
  const pending = new Map(), listeners = [];
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) {
      const { resolve: ok, reject } = pending.get(message.id); pending.delete(message.id);
      message.error ? reject(new Error(message.error.message)) : ok(message.result);
    } else for (const listener of listeners) listener(message);
  };
  const send = (method, params = {}, sessionId) => new Promise((ok, reject) => {
    const messageId = ++id; pending.set(messageId, { resolve: ok, reject });
    socket.send(JSON.stringify({ id: messageId, method, params, sessionId }));
  });
  const { targetId } = await send("Target.createTarget", { url: "about:blank" });
  const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
  const page = (method, params) => send(method, params, sessionId);
  await page("Page.enable"); await page("Runtime.enable");
  await send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloads, eventsEnabled: true });
  const consoleErrors = [];
  listeners.push(message => {
    if (message.method === "Runtime.exceptionThrown") consoleErrors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
    if (message.method === "Runtime.consoleAPICalled" && message.params.type === "error") consoleErrors.push(message.params.args.map(a => a.value ?? a.description).join(" "));
  });
  const evaluate = async (expression) => {
    const result = await page("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  const waitFor = async (expression, timeout = 20000) => {
    const end = Date.now() + timeout;
    for (;;) {
      const value = await evaluate(expression).catch(() => undefined);
      if (value) return value;
      if (Date.now() > end) return false;
      await sleep(100);
    }
  };
  const close = async () => { try { await send("Browser.close"); } catch { /* Closed. */ } child.kill(); await sleep(300); try { rmSync(profile, { recursive: true, force: true }); } catch { /* Locked. */ } };
  return { page, send, evaluate, waitFor, close, downloads, consoleErrors };
}

const servers = await startServers({ fixtures });
const browser = await launch();
const { page, evaluate, waitFor } = browser;
const open = async (path) => {
  await page("Page.navigate", { url: "about:blank" });
  await page("Page.navigate", { url: `${servers.siteOrigin}${path}` });
  await waitFor("document.readyState === 'complete'");
};
const state = () => evaluate("document.documentElement.dataset.state");
const video = expression => evaluate(`(() => { const v = document.getElementById('video'); return ${expression}; })()`);
const screenshot = async (name) => {
  if (!shots) return;
  mkdirSync(shots, { recursive: true });
  const { data } = await page("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(shots, `${name}.png`), Buffer.from(data, "base64"));
};
const mediaRequests = (from = 0) => servers.log.slice(from).filter(entry => entry.origin === "media" && entry.method === "GET");

try {
  await page("Emulation.setDeviceMetricsOverride", { width: 1280, height: 860, deviceScaleFactor: 1, mobile: false });

  // 1. Video: decrypt, start, stream only part of the file.
  const videoLink = links.video;
  const videoBytes = readFileSync(join(fixtures, "w", videoLink.fragment.slice(1, 23), "media")).length;
  await open(`/v/${videoLink.fragment}`);
  check("video: metadata decrypts and the player gets ready", !!(await waitFor("document.documentElement.dataset.state === 'ready'")), await state());
  check("video: the title comes from the encrypted metadata", (await evaluate("document.getElementById('title').textContent")) === videoLink.source.replace(/\.[^.]+$/, ""));
  check("video: streams with Media Source Extensions", (await evaluate("keaclipPlayer.stats().engine")) === "mse");
  await screenshot("1-ready");
  await evaluate("document.getElementById('video').play()");
  const started = await waitFor("document.getElementById('video').currentTime > 1.5", 15000);
  check("video: playback starts and advances", !!started, `t=${(await video("v.currentTime")).toFixed(2)}`);
  const afterStart = await evaluate("keaclipPlayer.stats()");
  check("video: only part of the file was fetched to start", afterStart.fetchedBytes < videoBytes * 0.7, `${afterStart.fetchedBytes} of ${videoBytes} bytes`);
  await screenshot("2-playing");

  // 2. Seek far ahead: only the records around the target are fetched, then playback continues there.
  const beforeSeek = servers.log.length;
  const duration = await video("v.duration");
  const target = Math.floor(duration * 0.8);
  await video(`(v.currentTime = ${target}, true)`);
  const resumed = await waitFor(`(() => { const v = document.getElementById('video'); return !v.seeking && v.currentTime > ${target + 1}; })()`, 15000);
  check("seek: playback resumes at the new position", !!resumed, `target ${target}s, now ${(await video("v.currentTime")).toFixed(2)}s`);
  const seekRanges = mediaRequests(beforeSeek).map(entry => entry.headers.range);
  const firstStart = Number(/bytes=(\d+)/.exec(seekRanges[0] || "")?.[1] || 0);
  check("seek: the first request after seeking starts near the target, not at the start", firstStart > videoBytes * 0.6, `${seekRanges.length} range request(s), first ${seekRanges[0]}`);
  check("seek: the buffered range includes the target", await video(`[...Array(v.buffered.length).keys()].some(i => v.buffered.start(i) <= ${target} + 0.5 && v.buffered.end(i) > ${target} + 1)`));
  await screenshot("3-seeked");

  // 3. Keyboard: Left arrow goes back 5 s.
  const beforeKey = await video("v.currentTime");
  await page("Input.dispatchKeyEvent", { type: "keyDown", key: "ArrowLeft", code: "ArrowLeft", windowsVirtualKeyCode: 37 });
  await page("Input.dispatchKeyEvent", { type: "keyUp", key: "ArrowLeft", code: "ArrowLeft", windowsVirtualKeyCode: 37 });
  await sleep(400);
  const afterKey = await video("v.currentTime");
  check("keyboard: the left arrow jumps back about 5 s", beforeKey - afterKey > 3.5 && beforeKey - afterKey < 6.5, `${beforeKey.toFixed(2)} -> ${afterKey.toFixed(2)}`);

  // 4. Download: the saved file is the exact uploaded copy (SHA-256 recorded by the .NET pipeline).
  await evaluate("document.getElementById('download').click()");
  let saved = null;
  for (let i = 0; i < 100 && !saved; i++) { await sleep(150); saved = readdirSync(browser.downloads).find(name => !name.endsWith(".crdownload") && !name.endsWith(".tmp")); }
  const savedHash = saved ? createHash("sha256").update(readFileSync(join(browser.downloads, saved))).digest("hex") : "";
  check("download: the decrypted file matches the uploaded copy", savedHash === videoLink.sha256, saved || "nothing saved");

  // 5. Audio-only file.
  await open(`/v/${links.audio.fragment}`);
  check("audio: gets ready", !!(await waitFor("document.documentElement.dataset.state === 'ready'")));
  await evaluate("document.getElementById('video').play()");
  check("audio: plays and shows the audio card", !!(await waitFor("document.getElementById('video').currentTime > 1")) && await evaluate("document.getElementById('player').classList.contains('audio')"));
  await screenshot("4-audio");

  // 6. A file that was converted (VP9/Opus to H.264/AAC).
  await open(`/v/${links.converted.fragment}`);
  check("converted: gets ready", !!(await waitFor("document.documentElement.dataset.state === 'ready'")));
  await evaluate("document.getElementById('video').play()");
  check("converted: plays", !!(await waitFor("document.getElementById('video').currentTime > 1")));

  // 7. Whole-file fallback (used where MSE cannot play the codecs), including seeking.
  await open(`/v/?engine=blob${videoLink.fragment}`);
  check("fallback: loads the whole file and gets ready", !!(await waitFor("document.documentElement.dataset.state === 'ready'", 30000)) && (await evaluate("keaclipPlayer.stats().engine")) === "blob");
  await evaluate("document.getElementById('video').play()");
  await video("(v.currentTime = 30, true)");
  check("fallback: seeks and plays", !!(await waitFor("document.getElementById('video').currentTime > 31", 15000)));

  // 8. Error messages.
  const wrong = videoLink.fragment.slice(0, -3) + (videoLink.fragment.slice(-3, -2) === "A" ? "B" : "A") + videoLink.fragment.slice(-2);
  await open(`/v/${wrong}`);
  await waitFor("document.documentElement.dataset.state === 'error'");
  check("wrong key: explains that the key does not match", (await evaluate("document.getElementById('state-title').textContent")) === "This link's key doesn't match");
  await screenshot("5-wrong-key");
  await open(`/v/#AAAAAAAAAAAAAAAAAAAAAA${videoLink.fragment.slice(23)}`);
  await waitFor("document.documentElement.dataset.state === 'error'");
  check("missing object: explains that the video is no longer available", (await evaluate("document.getElementById('state-title').textContent")) === "This video is no longer available");
  await open("/v/");
  await waitFor("document.documentElement.dataset.state === 'error'");
  check("no fragment: explains that the link is incomplete", (await evaluate("document.getElementById('state-title').textContent")) === "This link is incomplete");

  // 9. Phone layout.
  await page("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await open(`/v/${videoLink.fragment}`);
  await waitFor("document.documentElement.dataset.state === 'ready'");
  check("phone: no horizontal scrolling", await evaluate("document.documentElement.scrollWidth <= window.innerWidth"));
  await screenshot("6-phone");

  // 10. Privacy: the key never reaches either server, and media requests carry no referrer or cookies.
  const keys = Object.values(links).map(link => link.fragment.split(".")[1]).concat([wrong.split(".")[1]]);
  const leaks = servers.log.filter(entry => keys.some(key => JSON.stringify(entry).includes(key)));
  check("privacy: no request URL or header contains a key", leaks.length === 0, `${servers.log.length} requests checked`);
  check("privacy: media requests send no Referer or Cookie", servers.log.filter(e => e.origin === "media").every(e => !e.headers.referer && !e.headers.cookie));
  check("page: no script errors", browser.consoleErrors.length === 0, browser.consoleErrors.slice(0, 3).join(" | "));
} catch (error) {
  check("e2e run", false, error.stack || String(error));
} finally {
  await browser.close();
  await servers.close();
}

const failed = results.filter(result => !result.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
process.exit(failed.length ? 1 : 0);
