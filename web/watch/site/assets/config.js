// SPDX-License-Identifier: GPL-3.0-or-later. Part of KeaClip; this unminified file is its own source.
// Deployment settings. build/watch-site.ps1 writes the real media origin here (and into _headers) when the site is built.
export default Object.freeze({
  // Public origin of the R2 bucket (custom domain), without a trailing slash. Objects live at <mediaBase>/w/<id>/{meta,media}.
  mediaBase: "__MEDIA_ORIGIN__",
  // Largest file the whole-file fallback loads into memory, for browsers that cannot stream the codecs.
  blobLimitBytes: 600e6,
  mobileBlobLimitBytes: 200e6,
});
