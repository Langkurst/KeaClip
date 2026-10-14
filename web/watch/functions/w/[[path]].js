// SPDX-License-Identifier: GPL-3.0-or-later. Part of KeaClip; this unminified file is its own source.
// Cloudflare Pages Function: serves the encrypted watch-link objects from the R2 bucket bound as MEDIA, on the watch
// page's own origin (/w/<id>/meta and /w/<id>/media). No custom domain, public bucket or CORS rule is needed.
// Objects are ciphertext; the key stays in the link's #fragment and never reaches this code.

const ID = /^[A-Za-z0-9_-]{22}$/;
const PARTS = new Set(["meta", "media"]);

function plain(status, text, extra = {}) {
  return new Response(text, { status, headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store", ...extra } });
}

// One "bytes=a-b", "bytes=a-" or "bytes=-n" range; anything else is answered with the whole object.
export function parseRange(header, size) {
  const match = /^bytes=(\d*)-(\d*)$/.exec((header || "").trim());
  if (!match || (match[1] === "" && match[2] === "")) return null;
  let start, end;
  if (match[1] === "") { const suffix = Number(match[2]); if (suffix === 0) return "invalid"; start = Math.max(0, size - suffix); end = size - 1; }
  else { start = Number(match[1]); end = match[2] === "" ? size - 1 : Math.min(Number(match[2]), size - 1); }
  if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start > end || start >= size) return "invalid";
  return { offset: start, length: end - start + 1 };
}

export async function onRequest({ request, env, params }) {
  const method = request.method;
  if (method !== "GET" && method !== "HEAD") return plain(405, "Method not allowed.", { Allow: "GET, HEAD" });
  const path = Array.isArray(params.path) ? params.path : [params.path];
  if (path.length !== 2 || !ID.test(path[0]) || !PARTS.has(path[1])) return plain(404, "Not found.");
  const key = `w/${path[0]}/${path[1]}`;

  const head = await env.MEDIA.head(key);
  if (!head) return plain(404, "This link has expired or was removed.");
  const size = head.size;
  const headers = new Headers({
    "Content-Type": "application/octet-stream",
    "Accept-Ranges": "bytes",
    "Cache-Control": "private, max-age=3600",
    "X-Content-Type-Options": "nosniff",
    "ETag": head.httpEtag,
  });
  const range = parseRange(request.headers.get("Range"), size);
  if (range === "invalid") { headers.set("Content-Range", `bytes */${size}`); return new Response(null, { status: 416, headers }); }
  if (range) {
    headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${size}`);
    headers.set("Content-Length", String(range.length));
    if (method === "HEAD") return new Response(null, { status: 206, headers });
    const object = await env.MEDIA.get(key, { range });
    if (!object) return plain(404, "This link has expired or was removed.");
    return new Response(object.body, { status: 206, headers });
  }
  headers.set("Content-Length", String(size));
  if (method === "HEAD") return new Response(null, { status: 200, headers });
  const object = await env.MEDIA.get(key);
  if (!object) return plain(404, "This link has expired or was removed.");
  return new Response(object.body, { status: 200, headers });
}
