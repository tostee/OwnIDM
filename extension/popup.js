(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const state = await chrome.runtime.sendMessage({ type: "ownidm-get-media", tabId: tab.id });
  if (!state || state.error) {
    document.getElementById("list").textContent = "No disponible en esta pestaña.";
    return;
  }

  const enabled = document.getElementById("enabled");
  const videoSite = document.getElementById("videoSite");
  const minMB = document.getElementById("minMB");
  const list = document.getElementById("list");

  enabled.checked = state.enabled;
  videoSite.checked = state.videoEnabled;
  minMB.value = state.videoMinMB;
  document.getElementById("site").textContent = state.site || "esta página";

  enabled.addEventListener("change", () =>
    chrome.runtime.sendMessage({ type: "ownidm-set", values: { enabled: enabled.checked } }));
  videoSite.addEventListener("change", () =>
    chrome.runtime.sendMessage({ type: "ownidm-set-site", tabId: tab.id, site: state.site, enabled: videoSite.checked }));
  minMB.addEventListener("change", () =>
    chrome.runtime.sendMessage({ type: "ownidm-set", values: { videoMinMB: Math.max(0, Number(minMB.value) || 0) } }));

  if (!state.items.length) {
    list.innerHTML = '<div class="empty">Reproduce el vídeo y aparecerá aquí.</div>';
    return;
  }
  for (const item of state.items) {
    const row = document.createElement("div");
    row.className = "item";
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = item.fileName;
    name.title = item.url;
    const meta = document.createElement("span");
    meta.className = "meta";
    meta.textContent = item.protected ? "protegido (DRM)" : item.live ? "en directo" : item.label;
    const btn = document.createElement("button");
    btn.textContent = "Descargar";
    btn.disabled = item.protected || item.live;
    btn.addEventListener("click", async () => {
      btn.disabled = true;
      btn.textContent = "…";
      const resp = await chrome.runtime.sendMessage({ type: "ownidm-download", tabId: tab.id, id: item.id });
      btn.textContent = resp && resp.ok ? "✔" : "Descargar";
      btn.disabled = !!(resp && resp.ok);
    });
    row.append(name, meta, btn);
    list.appendChild(row);
  }
})();
