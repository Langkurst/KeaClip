// Test-only local servers for the watch page. Never deploy this.
//   site origin  http://127.0.0.1:<sitePort>  serves web/watch/site with the production security headers
//   media origin http://127.0.0.1:<mediaPort> serves encrypted objects from a fixtures folder like a public R2 bucket:
//                GET/HEAD with single byte ranges, CORS for the site origin only, 404 for missing objects.
// Every request to either origin is logged (method, URL, headers) so a check can prove the key never arrives.
// Run on its own: node web/watch/test/server.mjs <fixtures folder> [sitePort] [mediaPort]

import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { createReadStream } from "node:fs";
import { extname, join, normalize, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const SITE = resolve(fileURLToPath(new URL("../site/", import.meta.url)));
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml", ".txt": "text/plain; charset=utf-8" };

function inside(root, relative) {
  const path = normalize(join(root, relative));
  return path === root || path.startsWith(root.endsWith(sep) ? root : root + sep) ? path : null;
}

export async function startServers({ fixtures, sitePort = 0, mediaPort = 0, host = "127.0.0.1" } = {}) {
  const log = [];
  const record = (origin, request) => log.push({ origin, method: request.method, url: request.url, headers: { ...request.headers } });
  let siteOrigin = "", mediaOrigin = "";

  const media = createServer(async (request, response) => {
    record("media", request);
    response.setHeader("Access-Control-Allow-Origin", siteOrigin);
    response.setHeader("Vary", "Origin");
    response.setHeader("Access-Control-Expose-Headers", "Content-Range, Content-Length, ETag, Accept-Ranges");
    if (request.method === "OPTIONS") {
      response.setHeader("Access-Control-Allow-Methods", "GET, HEAD");
      response.setHeader("Access-Control-Allow-Headers", "range");
      response.setHeader("Access-Control-Max-Age", "600");
      response.writeHead(204).end();
      return;
    }
    if (request.method !== "GET" && request.method !== "HEAD") { response.writeHead(405).end(); return; }
    const url = new URL(request.url, "http://media");
    const path = /^\/w\/[A-Za-z0-9_-]{22}\/(meta|media)$/.test(url.pathname) ? inside(resolve(fixtures), url.pathname.slice(1)) : null;
    let size;
    try { size = path ? (await stat(path)).size : -1; } catch { size = -1; }
    if (size < 0) { response.writeHead(404, { "Content-Type": "application/xml" }).end("<Error><Code>NoSuchKey</Code></Error>"); return; }
    response.setHeader("Accept-Ranges", "bytes");
    response.setHeader("Content-Type", "application/octet-stream");
    response.setHeader("ETag", `"${size}"`);
    const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || "");
    if (request.headers.range && !range) { response.writeHead(416, { "Content-Range": `bytes */${size}` }).end(); return; }
    let start = 0, end = size - 1, status = 200;
    if (range) {
      start = Number(range[1]); end = range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
      if (start >= size || end < start) { response.writeHead(416, { "Content-Range": `bytes */${size}` }).end(); return; }
      status = 206;
      response.setHeader("Content-Range", `bytes ${start}-${end}/${size}`);
    }
    response.writeHead(status, { "Content-Length": String(end - start + 1) });
    if (request.method === "HEAD") { response.end(); return; }
    createReadStream(path, { start, end }).pipe(response);
  });

  const site = createServer(async (request, response) => {
    record("site", request);
    const url = new URL(request.url, "http://site");
    if (url.pathname === "/__log") { response.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify(log)); return; }
    response.setHeader("Content-Security-Policy", `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self' ${mediaOrigin}; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`);
    response.setHeader("Referrer-Policy", "no-referrer");
    response.setHeader("X-Content-Type-Options", "nosniff");
    response.setHeader("Cache-Control", "no-store");
    if (url.pathname === "/assets/config.js") {
      response.writeHead(200, { "Content-Type": TYPES[".js"] }).end(`export default Object.freeze({ mediaBase: ${JSON.stringify(mediaOrigin)}, blobLimitBytes: 600e6, mobileBlobLimitBytes: 200e6 });\n`);
      return;
    }
    let relative = decodeURIComponent(url.pathname);
    if (relative.endsWith("/")) relative += "index.html";
    const path = inside(SITE, relative.slice(1));
    try {
      const body = await readFile(path);
      response.writeHead(200, { "Content-Type": TYPES[extname(path)] || "application/octet-stream" }).end(body);
    } catch {
      response.writeHead(404, { "Content-Type": "text/plain" }).end("Not found");
    }
  });

  await new Promise(done => media.listen(mediaPort, host, done));
  await new Promise(done => site.listen(sitePort, host, done));
  // A different port makes the media origin cross-origin, so CORS is exercised as with a real bucket.
  mediaOrigin = `http://${host}:${media.address().port}`;
  siteOrigin = `http://127.0.0.1:${site.address().port}`;
  return {
    siteOrigin, mediaOrigin, log,
    close: () => Promise.all([new Promise(done => site.close(done)), new Promise(done => media.close(done))]),
  };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [fixtures, sitePort = "8787", mediaPort = "8788"] = process.argv.slice(2);
  if (!fixtures) { console.error("Usage: node server.mjs <fixtures folder> [sitePort] [mediaPort]"); process.exit(2); }
  const servers = await startServers({ fixtures, sitePort: Number(sitePort), mediaPort: Number(mediaPort) });
  console.log(`Watch page: ${servers.siteOrigin}/v/#<id>.<key>   media: ${servers.mediaOrigin}   (Ctrl+C to stop)`);
}
