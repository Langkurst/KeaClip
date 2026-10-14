"use strict";
function safeMediaUrl(value) {
  try { const url = new URL(value); return ["http:", "https:"].includes(url.protocol) && !url.username && !url.password && value.length <= 8192 ? url.href : null; }
  catch { return null; }
}
function handoff(value) {
  const url = safeMediaUrl(value);
  if (url) browser.tabs.create({ url: "keaclip://download?url=" + encodeURIComponent(url) });
}
browser.contextMenus.create({ id: "keaclip-send", title: "Send to KeaClip", contexts: ["page", "link", "video", "audio"] });
browser.contextMenus.onClicked.addListener((info) => { if (info.menuItemId === "keaclip-send") handoff(info.srcUrl || info.linkUrl || info.pageUrl); });
browser.browserAction.onClicked.addListener((tab) => handoff(tab.url));
