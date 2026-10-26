// OwnIDM browser integration (Manifest V3 service worker).
//  * Downloads started in the browser are paused, offered to OwnIDM through native
//    messaging and cancelled in the browser only if the user accepted them in OwnIDM.
//  * Videos played on a page (direct media files, HLS playlists, DASH manifests) are detected and
//    offered through an on-page panel and the toolbar popup, configurable per site.
//  * YouTube pages (which publish no downloadable stream) are offered as a whole to OwnIDM,
//    which downloads them with yt-dlp; the context menu does the same for any other page.

const HOST = "com.ownidm.host";
const DEFAULT_MIN_MB = 1;

// ------------------------------------------------------------------ settings

async function settings() {
  const s = await chrome.storage.local.get({ enabled: true, videoDisabledSites: [], videoMinMB: DEFAULT_MIN_MB });
  return s;
}

function hostOf(url) {
  try {
    return new URL(url).hostname.replace(/^www\./, "");
  } catch {
    return "";
  }
}

async function videoEnabledFor(pageUrl) {
  const { videoDisabledSites } = await settings();
  return !videoDisabledSites.includes(hostOf(pageUrl));
}

// ------------------------------------------------------------------ native messaging

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

/** Resolves true only when the user accepted the download in OwnIDM. */
async function sendToOwnIDM({ url, referrer, fileName, fileSize, kind }) {
  const msg = {
    type: "add",
    url,
    referrer: referrer || "",
    fileName: fileName || "",
    fileSize: fileSize > 0 ? fileSize : null,
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
    kind: kind || "file",
  };
  // A native messaging *port* (unlike sendNativeMessage) keeps the service worker alive
  // while the user decides in OwnIDM's dialog, which can take minutes.
  return new Promise((resolve) => {
    let port;
    try {
      port = chrome.runtime.connectNative(HOST);
    } catch (e) {
      console.warn("OwnIDM no disponible:", e && e.message);
      resolve(false);
      return;
    }
    let settled = false;
    const finish = (ok) => {
      if (settled) return;
      settled = true;
      try { port.disconnect(); } catch { /* already closed */ }
      resolve(ok);
    };
    port.onMessage.addListener((resp) => finish(!!(resp && resp.ok)));
    port.onDisconnect.addListener(() => {
      if (chrome.runtime.lastError) console.warn("OwnIDM no disponible:", chrome.runtime.lastError.message);
      finish(false);
    });
    port.postMessage(msg);
  });
}

// ------------------------------------------------------------------ download capture

function baseName(path) {
  return (path || "").split(/[\\/]/).pop();
}

chrome.downloads.onCreated.addListener(async (item) => {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url) || item.state !== "in_progress") return;
  if (!(await settings()).enabled) return;

  try {
    await chrome.downloads.pause(item.id);
  } catch {
    return; // already finished or gone
  }

  const accepted = await sendToOwnIDM({
    url,
    referrer: item.referrer,
    fileName: baseName(item.filename),
    fileSize: item.totalBytes,
  });
  try {
    if (accepted) {
      await chrome.downloads.cancel(item.id);
      await chrome.downloads.erase({ id: item.id });
    } else {
      await chrome.downloads.resume(item.id);
    }
  } catch (e) {
    console.warn("OwnIDM:", e && e.message);
  }
});

// ------------------------------------------------------------------ video detection

const MEDIA_EXT = {
  "video/mp4": "mp4", "video/webm": "webm", "video/x-matroska": "mkv", "video/quicktime": "mov",
  "video/x-flv": "flv", "video/ogg": "ogv", "audio/mpeg": "mp3", "audio/mp4": "m4a", "audio/aac": "aac",
  "audio/ogg": "ogg", "audio/webm": "weba", "audio/wav": "wav", "audio/flac": "flac",
};
const SEGMENT_RE = /\.(ts|m4s|m4f|cmfv|cmfa|aac|key)(\?|$)/i;

// Per-tab detections live in storage.session so they survive service worker restarts.
async function loadTab(tabId) {
  const key = `tab:${tabId}`;
  const data = await chrome.storage.session.get(key);
  return data[key] || { items: [], variants: [], nav: 0 };
}

async function saveTab(tabId, state) {
  await chrome.storage.session.set({ [`tab:${tabId}`]: state });
}

function header(headers, name) {
  const h = (headers || []).find((x) => x.name.toLowerCase() === name);
  return h ? h.value : "";
}

function totalSize(headers) {
  const range = header(headers, "content-range");
  const m = range && range.match(/\/(\d+)\s*$/);
  if (m) return Number(m[1]);
  return Number(header(headers, "content-length")) || 0;
}

function formatSize(bytes) {
  if (!bytes) return "";
  const units = ["B", "KB", "MB", "GB"];
  let v = bytes, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return `${v.toFixed(v < 10 && u > 0 ? 1 : 0)} ${units[u]}`;
}

function cleanTitle(title) {
  const t = (title || "video").replace(/[\\/:*?"<>|]+/g, " ").replace(/\s+/g, " ").trim();
  return (t || "video").slice(0, 120);
}

async function inspectPlaylist(url) {
  try {
    const resp = await fetch(url, { credentials: "include" });
    const text = await resp.text();
    if (!text.startsWith("#EXTM3U")) return null;
    const lines = text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
    if (text.includes("#EXT-X-STREAM-INF")) {
      const variants = [];
      let best = "";
      let bestBw = -1;
      for (let i = 0; i < lines.length; i++) {
        if (!lines[i].startsWith("#EXT-X-STREAM-INF")) continue;
        const uri = lines.slice(i + 1).find((l) => !l.startsWith("#"));
        if (!uri) continue;
        variants.push(new URL(uri, url).href);
        const bw = Number((lines[i].match(/BANDWIDTH=(\d+)/) || [])[1] || 0);
        const res = (lines[i].match(/RESOLUTION=(\d+x\d+)/) || [])[1] || "";
        if (bw > bestBw) { bestBw = bw; best = res; }
      }
      return { master: true, variants, label: `HLS · hasta ${best || formatSize(bestBw / 8) + "/s"} · ${variants.length} calidades` };
    }
    const encrypted = lines.some((l) => l.startsWith("#EXT-X-KEY") && !/METHOD=NONE/i.test(l));
    const live = !lines.some((l) => l.startsWith("#EXT-X-ENDLIST") || l.startsWith("#EXT-X-PLAYLIST-TYPE:VOD"));
    const seconds = lines.filter((l) => l.startsWith("#EXTINF:"))
      .reduce((sum, l) => sum + (parseFloat(l.slice(8)) || 0), 0);
    return { master: false, encrypted, live, label: `HLS · ${formatDuration(seconds)}` + (live ? " · en directo" : "") };
  } catch {
    return null;
  }
}

function formatDuration(seconds) {
  return `${Math.floor(seconds / 60)}:${String(Math.round(seconds % 60)).padStart(2, "0")}`;
}

/** xs:duration ("PT1H2M3.5S") in seconds. */
function isoSeconds(value) {
  const m = (value || "").match(/^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?)?$/);
  if (!m) return 0;
  return (+m[1] || 0) * 86400 + (+m[2] || 0) * 3600 + (+m[3] || 0) * 60 + (parseFloat(m[4]) || 0);
}

// Service workers have no DOMParser: a few regular expressions are enough for the panel label.
async function inspectManifest(url) {
  try {
    const resp = await fetch(url, { credentials: "include" });
    const text = await resp.text();
    if (!/<MPD[\s>]/.test(text)) return null;
    const live = /<MPD[^>]*\stype\s*=\s*["']dynamic["']/.test(text);
    const encrypted = /<(\w+:)?ContentProtection[\s>/]/.test(text);
    const seconds = isoSeconds((text.match(/mediaPresentationDuration\s*=\s*["']([^"']+)["']/) || [])[1]);
    const heights = [...text.matchAll(/<(?:\w+:)?Representation\b[^>]*\sheight\s*=\s*["'](\d+)["']/g)].map((m) => +m[1]);
    const best = heights.length ? `${Math.max(...heights)}p` : "";
    return {
      encrypted, live,
      label: "DASH" + (seconds ? ` · ${formatDuration(seconds)}` : "") + (best ? ` · hasta ${best}` : "") + (live ? " · en directo" : ""),
    };
  } catch {
    return null;
  }
}

// ------------------------------------------------------------------ pages for yt-dlp

/** Canonical address of a YouTube video page, or null for any other page. */
function youTubeVideo(url) {
  let u;
  try {
    u = new URL(url);
  } catch {
    return null;
  }
  const host = u.hostname.replace(/^(www|m|music)\./, "");
  if (host === "youtu.be" && u.pathname.length > 1) return `https://www.youtube.com/watch?v=${u.pathname.slice(1)}`;
  if (host !== "youtube.com") return null;
  // Drop list=, t=… so yt-dlp gets the one video and not its playlist.
  const v = u.searchParams.get("v");
  if (u.pathname === "/watch" && v) return `https://www.youtube.com/watch?v=${encodeURIComponent(v)}`;
  const m = u.pathname.match(/^\/(shorts|live)\/([\w-]+)/);
  return m ? `https://www.youtube.com/${m[1]}/${m[2]}` : null;
}

function youTubeTitle(title) {
  return cleanTitle((title || "").replace(/^\(\d+\)\s*/, "").replace(/\s*-\s*YouTube$/, ""));
}

/** Lists the YouTube video of the tab's page in the panel (once per page). */
async function offerPageVideo(tabId, pageUrl, title) {
  const url = youTubeVideo(pageUrl);
  if (!url || !(await videoEnabledFor(pageUrl))) return;
  const { nav } = await loadTab(tabId);
  await addDetection(tabId, nav || 0, {
    url, kind: "ytdlp", label: "YouTube · con yt-dlp", pageUrl, fileName: `${youTubeTitle(title)}.mp4`,
    protected: false, live: false, size: 0,
  });
}

/** YouTube changes the title after the address: keep the listed name in step. */
function renamePageVideo(tabId, title) {
  return withTabLock(tabId, async () => {
    const state = await loadTab(tabId);
    const item = state.items.find((i) => i.kind === "ytdlp");
    const name = `${youTubeTitle(title)}.mp4`;
    if (!item || item.fileName === name) return;
    item.fileName = name;
    await saveTab(tabId, state);
    await refreshTab(tabId, state);
  });
}

// Detections of one tab arrive concurrently; serialize their read-modify-write.
const tabLocks = new Map();
function withTabLock(tabId, fn) {
  const next = (tabLocks.get(tabId) || Promise.resolve()).then(fn, fn);
  tabLocks.set(tabId, next.catch(() => {}));
  return next;
}

function addDetection(tabId, nav, entry, variantsOfMaster = []) {
  return withTabLock(tabId, () => addDetectionLocked(tabId, nav, entry, variantsOfMaster));
}

async function addDetectionLocked(tabId, nav, entry, variantsOfMaster) {
  const state = await loadTab(tabId);
  if ((state.nav || 0) !== nav) return; // found on a page the tab has already left
  if (state.items.some((i) => i.url === entry.url) || state.variants.includes(entry.url)) return;
  if (variantsOfMaster.length) {
    // A master playlist replaces the per-quality playlists already listed.
    state.variants.push(...variantsOfMaster);
    state.items = state.items.filter((i) => !variantsOfMaster.includes(i.url));
  }
  entry.id = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  state.items.push(entry);
  await saveTab(tabId, state);
  await refreshTab(tabId, state);
}

async function refreshTab(tabId, state) {
  let tab;
  try {
    tab = await chrome.tabs.get(tabId);
  } catch {
    return;
  }
  const enabled = await videoEnabledFor(tab.url);
  const usable = state.items.filter((i) => !i.protected);
  await chrome.action.setBadgeBackgroundColor({ color: "#1F6FD8", tabId });
  await chrome.action.setBadgeText({ text: enabled && usable.length ? String(usable.length) : "", tabId });
  if (enabled) {
    chrome.tabs.sendMessage(tabId, { type: "ownidm-media", items: state.items }).catch(() => {});
  }
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    handleResponse(details).catch((e) => console.warn("OwnIDM:", e));
  },
  { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "other"] },
  ["responseHeaders"]
);

async function handleResponse(d) {
  if (d.tabId < 0 || (d.statusCode !== 200 && d.statusCode !== 206)) return;
  const type = header(d.responseHeaders, "content-type").split(";")[0].trim().toLowerCase();
  const path = new URL(d.url).pathname.toLowerCase();
  const isPlaylist = type.includes("mpegurl") || path.endsWith(".m3u8");
  const isManifest = type.includes("dash+xml") || path.endsWith(".mpd");
  // Most responses are neither: leave before any await so the worker is not kept busy.
  // Direct files only count when a <video>/<audio> element loads them; players built on
  // MediaSource fetch short segments with XHR, which are not downloadable files.
  if (!isPlaylist && !isManifest && (d.type !== "media" || SEGMENT_RE.test(path))) return;

  const known = await loadTab(d.tabId);
  if (known.items.some((i) => i.url === d.url) || known.variants.includes(d.url)) return; // no refetch
  const nav = known.nav || 0;

  let tab;
  try {
    tab = await chrome.tabs.get(d.tabId);
  } catch {
    return;
  }
  if (!(await videoEnabledFor(tab.url))) return;
  const title = cleanTitle(tab.title);

  if (isPlaylist) {
    const info = await inspectPlaylist(d.url);
    if (!info) return;
    await addDetection(d.tabId, nav, {
      url: d.url, kind: "hls", label: info.label, pageUrl: tab.url, fileName: `${title}.mp4`,
      protected: !!info.encrypted, live: !!info.live, size: 0,
    }, info.master ? info.variants : []);
    return;
  }

  if (isManifest) {
    const info = await inspectManifest(d.url);
    if (!info) return;
    await addDetection(d.tabId, nav, {
      url: d.url, kind: "dash", label: info.label, pageUrl: tab.url, fileName: `${title}.mp4`,
      protected: info.encrypted, live: info.live, size: 0,
    });
    return;
  }

  if (!type.startsWith("video/") && !type.startsWith("audio/") && !/\.(mp4|webm|mkv|mov|m4v|mp3|m4a|ogg|flac)$/.test(path)) return;
  const size = totalSize(d.responseHeaders);
  const { videoMinMB } = await settings();
  if (size && size < videoMinMB * 1024 * 1024) return;
  const ext = (path.match(/\.([a-z0-9]{2,4})$/) || [])[1] || MEDIA_EXT[type] || "mp4";
  await addDetection(d.tabId, nav, {
    url: d.url, kind: "file", label: `${ext.toUpperCase()}${size ? " · " + formatSize(size) : ""}`,
    pageUrl: tab.url, fileName: `${title}.${ext}`, size, protected: false,
  });
}

// A new page (or a new URL in a single-page app) starts with an empty list.
chrome.tabs.onUpdated.addListener(async (tabId, change, tab) => {
  if (change.url) {
    await withTabLock(tabId, async () => {
      const old = await loadTab(tabId);
      await saveTab(tabId, { items: [], variants: [], nav: (old.nav || 0) + 1 });
    });
    chrome.tabs.sendMessage(tabId, { type: "ownidm-media", items: [] }).catch(() => {});
    await chrome.action.setBadgeText({ text: "", tabId });
    await offerPageVideo(tabId, change.url, tab.title);
  } else if (change.status === "complete" && tab.url) {
    await offerPageVideo(tabId, tab.url, tab.title); // reloads, tabs open before the extension started
  }
  if (change.title && youTubeVideo(tab.url)) await renamePageVideo(tabId, change.title);
});

chrome.tabs.onRemoved.addListener((tabId) => {
  withTabLock(tabId, () => chrome.storage.session.remove(`tab:${tabId}`))
    .finally(() => tabLocks.delete(tabId));
});

async function downloadDetected(tabId, id) {
  const state = await loadTab(tabId);
  const item = state.items.find((i) => i.id === id);
  if (!item || item.protected) return false;
  return sendToOwnIDM({ url: item.url, referrer: item.pageUrl, fileName: item.fileName, fileSize: item.size, kind: item.kind });
}

// ------------------------------------------------------------------ messages from panel & popup

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    const tabId = msg.tabId ?? sender.tab?.id;
    switch (msg.type) {
      case "ownidm-panel-ready": {
        const state = await loadTab(tabId);
        if (state.items.length && sender.tab && (await videoEnabledFor(sender.tab.url))) {
          sendResponse({ items: state.items });
        } else {
          sendResponse({ items: [] });
        }
        return;
      }
      case "ownidm-get-media": {
        const tab = await chrome.tabs.get(tabId);
        await offerPageVideo(tabId, tab.url, tab.title);
        const s = await settings();
        sendResponse({
          items: (await loadTab(tabId)).items,
          site: hostOf(tab.url),
          videoEnabled: !s.videoDisabledSites.includes(hostOf(tab.url)),
          enabled: s.enabled,
          videoMinMB: s.videoMinMB,
        });
        return;
      }
      case "ownidm-download":
        sendResponse({ ok: await downloadDetected(tabId, msg.id) });
        return;
      case "ownidm-set-site": {
        const s = await settings();
        const sites = new Set(s.videoDisabledSites);
        if (msg.enabled) sites.delete(msg.site);
        else sites.add(msg.site);
        await chrome.storage.local.set({ videoDisabledSites: [...sites] });
        if (tabId != null) {
          await refreshTab(tabId, await loadTab(tabId));
          if (!msg.enabled) chrome.tabs.sendMessage(tabId, { type: "ownidm-hide" }).catch(() => {});
        }
        sendResponse({ ok: true });
        return;
      }
      case "ownidm-set": {
        await chrome.storage.local.set(msg.values);
        await updateBadge();
        sendResponse({ ok: true });
        return;
      }
      default:
        sendResponse({});
    }
  })().catch((e) => sendResponse({ error: String(e && e.message || e) }));
  return true; // async response
});

// ------------------------------------------------------------------ toolbar & context menu

async function updateBadge() {
  const { enabled } = await settings();
  await chrome.action.setTitle({
    title: enabled ? "OwnIDM: captura activada" : "OwnIDM: captura de descargas desactivada",
  });
}

chrome.runtime.onInstalled.addListener(async () => {
  await chrome.contextMenus.removeAll(); // an update keeps the old menus: avoid duplicate ids
  chrome.contextMenus.create({
    id: "ownidm-link",
    title: "Descargar con OwnIDM",
    contexts: ["link", "image", "video", "audio"],
  });
  chrome.contextMenus.create({
    id: "ownidm-page-video",
    title: "Descargar el vídeo de esta página con OwnIDM (yt-dlp)",
    contexts: ["page", "video"],
    documentUrlPatterns: ["http://*/*", "https://*/*"],
  });
  updateBadge();
});

chrome.runtime.onStartup.addListener(updateBadge);

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === "ownidm-page-video") {
    const page = youTubeVideo(info.pageUrl) || info.pageUrl;
    if (/^https?:/i.test(page)) {
      sendToOwnIDM({ url: page, referrer: info.pageUrl, fileName: `${youTubeTitle(tab && tab.title)}.mp4`, kind: "ytdlp" });
    }
    return;
  }
  const url = info.linkUrl || info.srcUrl;
  // magnet: links go to OwnIDM's torrent downloader.
  if (url && /^(https?:|magnet:\?)/i.test(url)) sendToOwnIDM({ url, referrer: info.pageUrl });
});
