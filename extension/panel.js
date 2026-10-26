// OwnIDM on-page "Descargar vídeo" panel. Lives in a closed shadow root so page styles
// cannot affect it (and it cannot affect the page).
(() => {
  if (window.top !== window || window.__ownidmPanel) return;
  window.__ownidmPanel = true;

  let host = null;
  let root = null;
  let items = [];
  let open = false;
  let dismissed = false;

  const CSS = `
    :host { all: initial; }
    .wrap { position: fixed; top: 14px; right: 14px; z-index: 2147483647; font: 13px/1.35 "Segoe UI", system-ui, sans-serif; color: #1d2633; }
    .btn { display: flex; align-items: center; gap: 8px; background: #1f6fd8; color: #fff; border: 0; border-radius: 8px;
           padding: 8px 12px; cursor: pointer; box-shadow: 0 4px 14px rgba(0,0,0,.25); font: inherit; font-weight: 600; }
    .btn:hover { background: #185db8; }
    .count { background: rgba(255,255,255,.25); border-radius: 10px; padding: 0 7px; font-size: 12px; }
    .x { background: transparent; border: 0; color: #fff; cursor: pointer; font-size: 15px; padding: 0 0 0 4px; opacity: .8; }
    .x:hover { opacity: 1; }
    .list { margin-top: 6px; background: #fff; border-radius: 8px; box-shadow: 0 6px 24px rgba(0,0,0,.25); min-width: 300px; max-width: 420px; overflow: hidden; }
    .row { display: flex; align-items: center; gap: 10px; padding: 9px 12px; border-bottom: 1px solid #eef1f5; cursor: pointer; }
    .row:hover { background: #e5f0fd; }
    .row.off { cursor: default; opacity: .55; }
    .row.off:hover { background: #fff; }
    .name { flex: 1; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
    .meta { color: #5f6b7a; font-size: 12px; white-space: nowrap; }
    .foot { display: flex; justify-content: space-between; padding: 7px 12px; background: #f5f7fa; font-size: 12px; }
    .link { color: #1f6fd8; cursor: pointer; background: none; border: 0; font: inherit; padding: 0; }
    .msg { padding: 9px 12px; color: #1e9e5a; }
  `;

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function render() {
    if (dismissed || !items.length) {
      if (host) host.style.display = "none";
      return;
    }
    if (!host) {
      host = document.createElement("ownidm-panel");
      root = host.attachShadow({ mode: "closed" });
      (document.body || document.documentElement).appendChild(host);
    }
    host.style.display = "";
    root.textContent = "";
    const style = el("style");
    style.textContent = CSS;
    root.appendChild(style);

    const wrap = el("div", "wrap");
    const usable = items.filter((i) => !i.protected);
    const btn = el("button", "btn");
    btn.append(el("span", null, "⬇ Descargar vídeo"), el("span", "count", String(usable.length)));
    const close = el("button", "x", "✕");
    close.title = "Ocultar en esta página";
    close.addEventListener("click", (e) => {
      e.stopPropagation();
      dismissed = true;
      render();
    });
    btn.appendChild(close);
    btn.addEventListener("click", () => {
      open = !open;
      render();
    });
    wrap.appendChild(btn);

    if (open) {
      const list = el("div", "list");
      for (const item of items) {
        const row = el("div", "row" + (item.protected || item.live ? " off" : ""));
        row.append(el("span", "name", item.fileName), el("span", "meta",
          item.protected ? "protegido (DRM)" : item.live ? "en directo" : item.label));
        if (!item.protected && !item.live) {
          row.addEventListener("click", async () => {
            row.querySelector(".meta").textContent = "esperando a OwnIDM…";
            const resp = await chrome.runtime.sendMessage({ type: "ownidm-download", id: item.id });
            row.querySelector(".meta").textContent = resp && resp.ok ? "añadido a OwnIDM ✔" : item.label;
          });
        }
        list.appendChild(row);
      }
      const foot = el("div", "foot");
      const off = el("button", "link", "No mostrar en este sitio");
      off.addEventListener("click", () => {
        chrome.runtime.sendMessage({ type: "ownidm-set-site", site: location.hostname.replace(/^www\./, ""), enabled: false });
        dismissed = true;
        render();
      });
      foot.append(off, el("span", "meta", "OwnIDM"));
      list.appendChild(foot);
      wrap.appendChild(list);
    }
    root.appendChild(wrap);
  }

  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.type === "ownidm-media") {
      const known = items.length;
      items = msg.items || [];
      if (items.length > known) dismissed = false; // a new video re-opens the button
      render();
    } else if (msg.type === "ownidm-hide") {
      dismissed = true;
      render();
    }
  });

  // Recover detections made before this script loaded (or after a page refresh of the panel).
  chrome.runtime.sendMessage({ type: "ownidm-panel-ready" }).then((resp) => {
    if (resp && resp.items) {
      items = resp.items;
      render();
    }
  }).catch(() => {});

  // Leave fullscreen players alone.
  document.addEventListener("fullscreenchange", () => {
    if (host) host.style.visibility = document.fullscreenElement ? "hidden" : "";
  });
})();
