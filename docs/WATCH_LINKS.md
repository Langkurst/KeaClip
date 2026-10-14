# Watch links

A watch link opens a KeaClip-branded page where the recipient watches the video straight away in the browser (custom player,
seeking, keyboard shortcuts, phone layout) and can download it. It looks like `https://watch.<your domain>/v/#<id>.<key>`.
File.Kiwi stays the separate "Share" (download link) option and is unchanged.

Status (2026-10-13): implemented and verified locally (tests and a real headless browser against local servers). **Not
deployed**: it needs the owner's Cloudflare account, bucket, domain and token (section 5). Nothing was uploaded to any real
service, and no account was created.

## 1. How it works

```
KeaClip (Windows)                                   Cloudflare (owner account)              Recipient's browser
-----------------                                   ----------------------------            -------------------
original file (only read)
  -> browser copy: fragmented MP4, H.264/AAC        R2 bucket (public read via custom       watch page (Pages, static)
     (FFmpeg; repackaged when already H.264/AAC,      domain, CORS for the page origin)       1. reads #<id>.<key> locally
      converted otherwise; app-owned temp folder)       w/<id>/media  encrypted video         2. GET  w/<id>/meta -> decrypt
  -> encrypt with a fresh 16-byte key ----SigV4----->   w/<id>/meta   encrypted metadata      3. Range GETs of w/<id>/media
     (seekable RFC 8188, 64 KiB records)  PUT/multipart  lifecycle: delete w/ after N days    4. decrypt records (WebCrypto)
  -> link = player page + #<id>.<key>                                                         5. Media Source Extensions
```

- **Desktop** (`src/Klippi.Infrastructure/Sharing/WatchLinkService.cs`): loads the setup, makes the browser copy
  (`MediaService.PrepareWatchCopyAsync`, a new partial file), indexes its fragments (`Fmp4Index`), encrypts it record by
  record (`WatchLinkEncryptingStream`) and uploads it with a dependency-free SigV4 S3 client (`S3CompatibleObjectStore`,
  `AwsSigV4`): one PUT up to 8 MiB, otherwise multipart with equal 8 MiB parts (R2 requires equal parts). Media goes up
  first; the small metadata object last, so a link only works once everything is there. A failed or cancelled upload is
  aborted (multipart) or deleted (media without metadata). The copy is deleted afterwards; the original is never written.
- **Browser** (`web/watch/site`): plain HTML/CSS/JS modules, no build step, no third-party code or CDN, strict CSP
  (`script-src 'self'`, no inline script or style). `ece.js` decrypts, `media.js` fetches byte ranges and caches decrypted
  records, `engine.js` streams through MSE (`ManagedMediaSource` on Safari) and falls back to a whole-file Blob, and
  `player.js` is the UI.
- **App UI**: Editor, Share card, **Create watch link** (latest export, else the open file). Settings, **Watch links**:
  storage endpoint, bucket, access key ID, secret, player page and "delete links after N days".

### Object format

- `w/<id>/media`: one RFC 8188 `aes128gcm` message over the whole copy. Header: 16-byte random salt, record size 65,536,
  empty key id. Key and nonce come from HKDF-SHA-256 of the link key (as in RFC 8188); each record is AES-128-GCM with the
  nonce XOR its sequence number. Every record except the last holds exactly 65,519 plaintext bytes plus delimiter 1 (no
  padding); the last ends with delimiter 2. So plaintext byte `p` is in record `p / 65519`, at ciphertext offset
  `21 + record * 65536`, and any byte range maps to whole records fetched with one Range request. It is still a standard
  RFC 8188 message (the .NET File.Kiwi decryptor reads it too).
- `w/<id>/meta`: a separate small RFC 8188 message (own salt, same key) with JSON: `name`, `kind` (video/audio), `mime`
  with the RFC 6381 codecs string, `size`, `end` (end of the last fragment), `init` (length of `ftyp`+`moov`), `duration`,
  `width`/`height`, `frags` (`[seconds, byte offset]` of every `moof`) and `expires`.
- `<id>` is 16 random bytes and the key is 16 random bytes, both base64url (22 characters).

The copy uses `-movflags +frag_keyframe+empty_moov+default_base_moof` (video: fragments start at keyframes, at least
2 s; converted video gets a keyframe every 2 s; audio: 2 s fragments). A repackaged file whose keyframes are more than 12 s
apart (or with a fragment over 48 MB) is converted instead, so a seek never has to fetch a huge fragment. Metadata,
chapters, subtitles and cover art are left out of the copy.

### Seeking and buffering

The player keeps about 30 s (at most 48 MB) ahead and 30 s behind, fetching whole fragments in requests of up to 4 MB
(1 MB for a quick start). A seek outside the buffer aborts the running fetch and starts at the fragment that contains the
target, so only the records around it are downloaded. Small gaps (B-frame start delay) are jumped automatically. The seek
bar shows every buffered range.

## 2. Privacy and security

- **End-to-end encrypted.** The key exists only in the link's `#fragment`. Browsers never send the fragment to any server,
  so neither the page host nor storage receives it. Storage holds only ciphertext; the file name, duration and codecs are
  inside the encrypted metadata. The page fetches with `credentials: "omit"` and `Referrer-Policy: no-referrer`, and wipes
  the raw key after importing it as non-extractable WebCrypto keys.
- **Integrity.** AES-GCM authenticates every record; wrong keys, changed bytes, swapped records and a stream cut at a
  record boundary are all rejected (tests in `web/watch/test/ece.test.mjs`).
- **What is not hidden.** Storage and the CDN see request IPs, times, object sizes and which byte ranges are read
  (roughly the length and bitrate). Anyone who has the full link can watch until the object is deleted: treat the link as
  a secret. It is not saved in KeaClip's history. The recipient's browser history contains it.
- **Credentials.** Use an R2 token with **Object Read & Write** on this one bucket only. The secret is stored only as a
  Windows DPAPI blob for the current user (`watch-links.json` in KeaClip's data folder, with KeaClip-specific entropy),
  decrypted only for an upload, never shown again and never logged. Error messages carry the HTTP status only.
- **Page hardening** (`_headers`): CSP `default-src 'none'`, scripts and styles only from the page itself, `connect-src`
  only the page and the bucket origin, `media-src blob:`, `frame-ancestors 'none'`, `noindex`.

## 3. Expiry and clean-up

- The bucket deletes links: add an R2 **object lifecycle rule** for prefix `w/` that deletes objects after N days, and enter
  the same N in KeaClip (Settings, Watch links, "Delete links after"). KeaClip shows "Available until about <date>" from
  that number; the bucket decides. Cloudflare documents that expired objects are typically removed within 24 hours.
- R2 aborts incomplete multipart uploads after 7 days by default; KeaClip also aborts them itself when an upload fails or is
  cancelled, and deletes the media object if the metadata upload fails.
- Objects are uploaded without `Cache-Control`. If you add a Cloudflare Cache Rule for the media domain, keep its edge TTL
  at most a day so deleted links stop working soon after.
- To remove one link early: delete `w/<id>/` in the R2 dashboard (`<id>` is the part between `#` and `.`).

## 4. Verified locally (2026-10-13)

| Check | Command | Result |
| --- | --- | --- |
| Record layout, golden vectors (.NET = independent Python implementation, incl. RFC 8188 example 3.1), SigV4 against AWS's four published S3 examples and the signing-key example, fMP4 index, S3 multipart/retry/abort, DPAPI store, service flow | `dotnet test tests/Klippi.Tests -c Release --no-build` | pass |
| Browser decryptor on the same vectors, range reads, wrong key, tampering, truncation | `node --test web/watch/test/ece.test.mjs` (also run from the .NET suite when Node is installed) | 14/14 |
| Real FFmpeg browser copies (repackage, convert VP9/Opus and long-GOP H.264, audio-only MP3 with cover) | `KLIPPI_FFMPEG_DIR=<ffmpeg bin>` + the .NET suite | pass |
| Real browser (headless Edge) against local servers: decrypt + start, partial fetch (8.3 of 23.3 MB at start), seek to 80 % fetching from the target only, keyboard seek, download hash = uploaded copy, audio-only, converted file, Blob fallback with seek, wrong-key / expired / incomplete-link messages, phone layout, no key in any request URL or header, no Referer/Cookie to storage, no script errors | see below | 23/23 |
| App wiring: setup validation, secret cleared from the form, Settings redirect when not set up, share card, link opens only on the configured page | `dotnet test tests/Klippi.UiTests -c Release --no-build` | pass |

Repeat the browser check:

```powershell
$env:KLIPPI_FFMPEG_DIR = "<folder with ffmpeg.exe and ffprobe.exe>"
$env:KLIPPI_WATCH_FIXTURES = "$PWD\artifacts\watch-fixtures"
dotnet test tests/Klippi.Tests -c Release --filter "FullyQualifiedName~Exports_encrypted_fixtures"
node web/watch/test/e2e.mjs artifacts/watch-fixtures artifacts/watch-shots   # screenshots in artifacts/watch-shots
node web/watch/test/server.mjs artifacts/watch-fixtures                       # manual: open http://127.0.0.1:8787/v/<fragment from links.json>
```

`server.mjs` is test-only: it serves the page with the production headers and the fixtures from a second origin with
Range and CORS like R2, and logs every request. Never deploy it.

## 5. What the owner needs to set up (not done)

**Recommended route, no domain needed (same origin):** the page's own Pages Function
(`web/watch/functions/w/[[path]].js`, tests in `web/watch/test/function.test.mjs`) serves `/w/<id>/{meta,media}`
from the bucket bound as `MEDIA`, with single byte ranges, so steps 1 (domain), 3 (public custom domain), 4 (CORS)
and 8 are skipped:

1. Cloudflare account with R2 enabled (the owner: sign-up and payment method).
2. `npx wrangler login`, then `npx wrangler r2 bucket create keaclip-watch` and the lifecycle rule (step 5 below).
3. `./build/watch-site.ps1 -SameOrigin`, then in `artifacts/watch-site`: `npx wrangler pages project create keaclip-watch --production-branch main`
   and `npx wrangler pages deploy --branch main` (the R2 binding comes from the generated `wrangler.toml`).
4. API token (step 6 below), then KeaClip Settings, Watch links, with player page `https://keaclip-watch.pages.dev/v/`.

The custom-domain route below still works (`-MediaOrigin`).

**Staying inside the free allowance (set up 2026-10-14):**

- Storage (10 GB-month free): KeaClip records every upload in `watch-usage.json` and refuses any upload that would take
  what may still be in the bucket past 8 GB. Each upload counts for at least 7 days (the bucket rule) plus one day of
  lifecycle lag; a damaged record stops uploads instead of forgetting them. Lifecycle rules: `w/` expires after 7 days,
  unfinished multipart uploads are aborted after 1 day. Public `r2.dev` access is off.
- Class A (1M/month free): bounded by the storage cap: at most about 31 x 8 GB of uploads a month, roughly 31,000 parts
  of 8 MB. Deletes are free.
- Class B (10M/month free): only the Pages Function reads the bucket, at most two reads per request, and the account's
  Workers plan is **Free**, which stops (not bills) requests after 100,000 a day: at most about 6.2M reads a month.
  Keep the Workers plan on Free; moving to Workers Paid removes this ceiling.
- Egress is free on R2.

Costs, limits and dashboard wording are live service data: check the current R2 and Pages pricing pages first. R2 bills
storage and operations, not egress.

1. **Cloudflare account** with a domain on Cloudflare (needed for custom domains), for example `example.nz`.
2. **R2 bucket**, for example `keaclip-watch`: `npx wrangler r2 bucket create keaclip-watch`.
3. **Public read through a custom domain** (not the `r2.dev` URL, which is for development): bucket, Settings, Custom
   Domains, Add, `media.example.nz` (or `npx wrangler r2 bucket domain add keaclip-watch --domain media.example.nz --zone-id <zone id>`).
   Objects are ciphertext with random names; there is no public listing.
4. **CORS rule** on the bucket (Settings, CORS Policy, JSON), with the page origin from step 7:

   ```json
   [
     {
       "AllowedOrigins": ["https://watch.example.nz"],
       "AllowedMethods": ["GET", "HEAD"],
       "AllowedHeaders": ["range"],
       "ExposeHeaders": ["Content-Range", "Content-Length", "Accept-Ranges", "ETag"],
       "MaxAgeSeconds": 86400
     }
   ]
   ```

   `Content-Range` must be exposed: the player reads the object size from it.
5. **Lifecycle rule**: bucket, Settings, Object Lifecycle Rules, Add rule: prefix `w/`, delete objects after 7 days (or
   another N), or `npx wrangler r2 bucket lifecycle add keaclip-watch delete-watch-links w/ --expire-days 7`.
6. **API token**: R2, Manage API Tokens, create an Account API token with **Object Read & Write**, scoped to
   `keaclip-watch` only. Keep the Access Key ID and Secret Access Key for step 9. The S3 endpoint is
   `https://<account id>.r2.cloudflarestorage.com` (EU jurisdiction buckets: `.eu.r2.cloudflarestorage.com`).
7. **Pages project for the player**: build the site with the media origin, then deploy it:

   ```powershell
   ./build/watch-site.ps1 -MediaOrigin https://media.example.nz      # writes artifacts/watch-site
   npx wrangler login
   npx wrangler pages project create keaclip-watch --production-branch main
   npx wrangler pages deploy artifacts/watch-site --project-name keaclip-watch --branch main
   ```

   The script fills `assets/config.js` and the CSP `connect-src` in `_headers`, adds `LICENSE.txt` (the page ships
   KeaClip's GPL-3.0-or-later JavaScript unminified, so the served files are their own source) and `build-info.json`.
8. **Optional custom domain for the page**: Pages project, Custom domains, `watch.example.nz`. Use that origin in the CORS
   rule (step 4); otherwise use the `https://keaclip-watch.pages.dev` origin.
9. **KeaClip**: Settings, Watch links: storage endpoint (step 6), bucket, access key ID, secret, player page
   `https://watch.example.nz/v/`, delete after 7 days (same as step 5). Save, then Editor, Create watch link, and open the
   link on a phone and a PC. Verified live 2026-10-14 (same-origin route, KeaClip 0.2.2): a 145 MB 1080p, 6:13 video uploaded, decrypted and played in the browser, seeking to 3:20 kept playing, all 17 media requests were 206 ranges on the page origin and the key appeared in no request URL. Expiry after 7 days is not yet observed.

Updating the page later is the same `build/watch-site.ps1` plus `wrangler pages deploy` (links keep working: the format is
versioned in the metadata, `v: 1`).

## 6. Known limitations

- Not deployed; no real R2 upload, CDN behaviour or expiry has been observed yet (section 5, step 9).
- Files that are not H.264 (8-bit 4:2:0) with AAC are converted with libx264 at "veryfast", which takes CPU time (below
  normal priority). HDR sources are converted without tone mapping, so colours can look flat.
- Browsers without MSE for the codecs (for example iPhones before iOS 17.1) load the whole file first, up to 600 MB on a
  PC and 200 MB on touch devices; above that they get the Download button only.
- The download is the browser copy (MP4/M4A), not the original file. Large downloads in Chrome/Edge stream to disk with
  the save dialog; other browsers build the file in memory.
- An interrupted upload starts again from the beginning; there is no in-app list to delete a link early (use the R2
  dashboard), no password option and no view counter.
