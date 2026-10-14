// Tests for the Pages Function that serves watch-link objects from R2 (web/watch/functions/w/[[path]].js).
// Run: node --test web/watch/test/function.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { onRequest, parseRange } from "../functions/w/[[path]].js";

const ID = "AbCdEfGhIjKlMnOpQrStUv"; // 22 base64url characters, like WatchLinkService ids
const bytes = Uint8Array.from({ length: 1000 }, (_, i) => i % 251);

// The parts of the R2 binding the function uses: head() and get() with an optional { offset, length } range.
function bucket(objects) {
  const calls = [];
  return {
    calls,
    async head(key) { calls.push(["head", key]); const data = objects[key]; return data ? { size: data.length, httpEtag: '"e1"' } : null; },
    async get(key, options = {}) {
      calls.push(["get", key, options.range]);
      const data = objects[key]; if (!data) return null;
      const { offset = 0, length = data.length - offset } = options.range || {};
      return { body: data.slice(offset, offset + length) };
    },
  };
}

function call(path, { method = "GET", range, objects = { [`w/${ID}/media`]: bytes } } = {}) {
  const MEDIA = bucket(objects);
  const headers = range ? { Range: range } : {};
  const request = new Request(`https://keaclip-watch.pages.dev/w/${path}`, { method, headers });
  return onRequest({ request, env: { MEDIA }, params: { path: path.split("/") } }).then(response => ({ response, MEDIA }));
}

test("a byte range is served as 206 with the size in Content-Range", async () => {
  const { response, MEDIA } = await call(`${ID}/media`, { range: "bytes=10-19" });
  assert.equal(response.status, 206);
  assert.equal(response.headers.get("Content-Range"), "bytes 10-19/1000");
  assert.equal(response.headers.get("Accept-Ranges"), "bytes");
  assert.deepEqual(new Uint8Array(await response.arrayBuffer()), bytes.slice(10, 20));
  assert.deepEqual(MEDIA.calls.at(-1), ["get", `w/${ID}/media`, { offset: 10, length: 10 }]);
});

test("open-ended and suffix ranges are clamped to the object", async () => {
  const open = (await call(`${ID}/media`, { range: "bytes=990-" })).response;
  assert.equal(open.headers.get("Content-Range"), "bytes 990-999/1000");
  assert.equal((await open.arrayBuffer()).byteLength, 10);
  const past = (await call(`${ID}/media`, { range: "bytes=995-5000" })).response;
  assert.equal(past.headers.get("Content-Range"), "bytes 995-999/1000");
  const suffix = (await call(`${ID}/media`, { range: "bytes=-4" })).response;
  assert.equal(suffix.headers.get("Content-Range"), "bytes 996-999/1000");
});

test("no range returns the whole object; HEAD returns no body", async () => {
  const whole = (await call(`${ID}/media`)).response;
  assert.equal(whole.status, 200);
  assert.equal((await whole.arrayBuffer()).byteLength, 1000);
  const { response, MEDIA } = await call(`${ID}/media`, { method: "HEAD" });
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("Content-Length"), "1000");
  assert.equal(MEDIA.calls.some(([kind]) => kind === "get"), false);
});

test("an unsatisfiable range is 416 with the size", async () => {
  const { response } = await call(`${ID}/media`, { range: "bytes=1000-1001" });
  assert.equal(response.status, 416);
  assert.equal(response.headers.get("Content-Range"), "bytes */1000");
});

test("only w/<22-char id>/meta or media is served; nothing else reaches the bucket", async () => {
  for (const path of [`${ID}/other`, `${ID}`, `${ID}/media/x`, `short/media`, `${ID.slice(0, 21)}!/media`, `../${ID}/media`]) {
    const { response, MEDIA } = await call(path);
    assert.equal(response.status, 404, path);
    assert.equal(MEDIA.calls.length, 0, path);
  }
});

test("a missing (expired) object is 404 and other methods are refused", async () => {
  assert.equal((await call(`${ID}/meta`, { objects: {} })).response.status, 404);
  const post = (await call(`${ID}/media`, { method: "POST" })).response;
  assert.equal(post.status, 405);
  assert.equal(post.headers.get("Allow"), "GET, HEAD");
});

test("range parsing", () => {
  assert.equal(parseRange(null, 10), null);
  assert.equal(parseRange("bytes=1-2,4-5", 10), null);
  assert.equal(parseRange("items=1-2", 10), null);
  assert.equal(parseRange("bytes=5-2", 10), "invalid");
  assert.equal(parseRange("bytes=-0", 10), "invalid");
  assert.deepEqual(parseRange("bytes=0-0", 10), { offset: 0, length: 1 });
});
