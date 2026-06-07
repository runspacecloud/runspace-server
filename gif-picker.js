// === RunSpace GIF Picker + Attachment Menu ===
// Drop-in: replaces the 📎 button with a "+" menu containing file attach + GIF picker
// Add <script src="/gif-picker.js"></script> after file-upload.js in chatt.html

(function() {
  var GIF_FAV_KEY = "runspace_gif_favorites";
  var GIF_RECENT_KEY = "runspace_gif_recent";
  var isOpen = false, gifPanelOpen = false;

  // === Load/save favorites ===
  function getFavs() { try { return JSON.parse(localStorage.getItem(GIF_FAV_KEY) || "[]"); } catch(e) { return []; } }
  function saveFavs(arr) { localStorage.setItem(GIF_FAV_KEY, JSON.stringify(arr.slice(0, 50))); }
  function isFav(url) { return getFavs().some(function(g) { return g.url === url; }); }
  function toggleFav(url, name) {
    var favs = getFavs();
    var idx = favs.findIndex(function(g) { return g.url === url; });
    if (idx >= 0) favs.splice(idx, 1);
    else favs.unshift({ url: url, name: name || "gif", ts: Date.now() });
    saveFavs(favs);
    return idx < 0;
  }
  function getRecent() { try { return JSON.parse(localStorage.getItem(GIF_RECENT_KEY) || "[]"); } catch(e) { return []; } }
  function addRecent(url, name) {
    var arr = getRecent().filter(function(g) { return g.url !== url; });
    arr.unshift({ url: url, name: name || "gif", ts: Date.now() });
    localStorage.setItem(GIF_RECENT_KEY, JSON.stringify(arr.slice(0, 30)));
  }

  // === Inject CSS ===
  var style = document.createElement("style");
  style.textContent = [
    ".attach-menu-wrap{position:relative;display:inline-flex}",
    ".attach-menu{position:absolute;bottom:calc(100% + 6px);left:0;background:#111827;border:1px solid #1e293b;border-radius:10px;padding:4px;min-width:160px;box-shadow:0 8px 30px rgba(0,0,0,.5);display:none;z-index:20;flex-direction:column}",
    ".attach-menu.show{display:flex}",
    ".attach-menu-item{display:flex;align-items:center;gap:8px;padding:8px 12px;border-radius:7px;border:none;background:none;color:#94a3b8;font-size:13px;font-weight:600;cursor:pointer;text-align:left;transition:background .1s,color .1s;white-space:nowrap}",
    ".attach-menu-item:hover{background:rgba(255,255,255,.06);color:#e2e8f0}",
    ".attach-menu-item svg{width:18px;height:18px;flex-shrink:0}",
    ".gif-panel{position:absolute;bottom:calc(100% + 6px);left:0;width:360px;max-width:90vw;height:420px;background:#111827;border:1px solid #1e293b;border-radius:12px;box-shadow:0 12px 40px rgba(0,0,0,.5);display:none;flex-direction:column;z-index:25;overflow:hidden}",
    ".gif-panel.show{display:flex}",
    ".gif-panel-head{padding:10px 12px;border-bottom:1px solid #1e293b;display:flex;align-items:center;gap:8px;flex-shrink:0}",
    ".gif-panel-tabs{display:flex;gap:2px;flex-shrink:0}",
    ".gif-tab{padding:5px 10px;border-radius:6px;border:none;background:none;color:#64748b;font-size:11px;font-weight:700;cursor:pointer;transition:all .15s}",
    ".gif-tab:hover{color:#94a3b8;background:rgba(255,255,255,.04)}",
    ".gif-tab.active{background:rgba(59,130,246,.1);color:#3b82f6}",
    ".gif-search{flex:1;height:32px;padding:0 10px;border-radius:7px;border:1px solid #1e293b;background:#0a0f1a;color:#e2e8f0;font-size:12px;outline:none}",
    ".gif-search:focus{border-color:#334155}",
    ".gif-search::placeholder{color:#64748b}",
    ".gif-panel-body{flex:1;overflow-y:auto;padding:8px;min-height:0}",
    ".gif-grid{display:grid;grid-template-columns:repeat(2,1fr);gap:6px}",
    ".gif-grid-item{position:relative;border-radius:8px;overflow:hidden;cursor:pointer;border:2px solid transparent;transition:border-color .15s,transform .1s;aspect-ratio:16/10;background:#1a2236}",
    ".gif-grid-item:hover{border-color:#3b82f6;transform:scale(1.02)}",
    ".gif-grid-item img{width:100%;height:100%;object-fit:cover;display:block}",
    ".gif-grid-item .gif-fav-btn{position:absolute;top:4px;right:4px;width:24px;height:24px;border-radius:50%;border:none;background:rgba(0,0,0,.6);color:#64748b;font-size:13px;cursor:pointer;display:none;align-items:center;justify-content:center;transition:color .15s}",
    ".gif-grid-item:hover .gif-fav-btn{display:flex}",
    ".gif-grid-item .gif-fav-btn:hover{color:#f59e0b}",
    ".gif-grid-item .gif-fav-btn.is-fav{color:#f59e0b;display:flex}",
    ".gif-empty{text-align:center;padding:40px 20px;color:#64748b;font-size:12px}",
    ".gif-upload-area{display:flex;flex-direction:column;align-items:center;gap:8px;padding:24px;border:2px dashed #1e293b;border-radius:10px;margin:8px;cursor:pointer;transition:border-color .15s}",
    ".gif-upload-area:hover{border-color:#334155}",
    ".gif-upload-area svg{color:#64748b}",
    ".gif-upload-hint{font-size:11px;color:#64748b}"
  ].join("\n");
  document.head.appendChild(style);

  // === Replace the 📎 button ===
  function init() {
    var oldBtn = document.getElementById("imageBtn");
    if (!oldBtn) return;
    var parent = oldBtn.parentElement;

    // Create wrapper
    var wrap = document.createElement("div");
    wrap.className = "attach-menu-wrap";

    // "+" button
    var plusBtn = document.createElement("button");
    plusBtn.id = "attachMenuBtn";
    plusBtn.className = "composer-file-btn";
    plusBtn.title = "Bifoga";
    plusBtn.innerHTML = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>';

    // Dropdown menu
    var menu = document.createElement("div");
    menu.className = "attach-menu";
    menu.id = "attachMenu";
    menu.innerHTML = [
      '<button class="attach-menu-item" id="amFile">',
        '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/></svg>',
        'Bifoga fil',
      '</button>',
      '<button class="attach-menu-item" id="amGif">',
        '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="2" y="2" width="20" height="20" rx="5"/><path d="M9.5 8v8M9.5 12H12M15 8v8"/></svg>',
        'GIF',
      '</button>'
    ].join("");

    // GIF panel
    var gifPanel = document.createElement("div");
    gifPanel.className = "gif-panel";
    gifPanel.id = "gifPanel";
    gifPanel.innerHTML = [
      '<div class="gif-panel-head">',
        '<div class="gif-panel-tabs">',
          '<button class="gif-tab active" data-tab="recent">Senaste</button>',
          '<button class="gif-tab" data-tab="favs">Favoriter</button>',
          '<button class="gif-tab" data-tab="upload">Ladda upp</button>',
        '</div>',
      '</div>',
      '<div class="gif-panel-body" id="gifPanelBody"></div>'
    ].join("");

    // Insert into DOM
    wrap.appendChild(plusBtn);
    wrap.appendChild(menu);
    wrap.appendChild(gifPanel);
    parent.insertBefore(wrap, oldBtn);
    oldBtn.style.display = "none";

    // === Event handlers ===
    plusBtn.addEventListener("click", function(e) {
      e.stopPropagation();
      if (gifPanelOpen) { closeGifPanel(); return; }
      isOpen = !isOpen;
      menu.classList.toggle("show", isOpen);
    });

    document.getElementById("amFile").addEventListener("click", function() {
      closeAll();
      document.getElementById("imageInput").click();
    });

    document.getElementById("amGif").addEventListener("click", function(e) {
      e.stopPropagation();
      closeMenu();
      gifPanelOpen = true;
      gifPanel.classList.add("show");
      renderGifTab("recent");
    });

    // Tab switching
    gifPanel.querySelectorAll(".gif-tab").forEach(function(tab) {
      tab.addEventListener("click", function(e) {
        e.stopPropagation();
        gifPanel.querySelectorAll(".gif-tab").forEach(function(t) { t.classList.remove("active"); });
        tab.classList.add("active");
        renderGifTab(tab.dataset.tab);
      });
    });

    // Close on outside click
    document.addEventListener("click", function(e) {
      if (!e.target.closest(".attach-menu-wrap")) closeAll();
    });

    // Stop propagation inside panel
    gifPanel.addEventListener("click", function(e) { e.stopPropagation(); });
    menu.addEventListener("click", function(e) { e.stopPropagation(); });
  }

  function closeMenu() { isOpen = false; var m = document.getElementById("attachMenu"); if (m) m.classList.remove("show"); }
  function closeGifPanel() { gifPanelOpen = false; var p = document.getElementById("gifPanel"); if (p) p.classList.remove("show"); }
  function closeAll() { closeMenu(); closeGifPanel(); }

  // === Render GIF tabs ===
  function renderGifTab(tab) {
    var body = document.getElementById("gifPanelBody");
    if (!body) return;

    if (tab === "recent") {
      var recent = getRecent();
      if (!recent.length) {
        body.innerHTML = '<div class="gif-empty">Inga GIFs ännu.<br>Ladda upp eller skicka GIFs så visas de här!</div>';
        return;
      }
      renderGifGrid(body, recent);
    }
    else if (tab === "favs") {
      var favs = getFavs();
      if (!favs.length) {
        body.innerHTML = '<div class="gif-empty">Inga favoriter ännu.<br>Hovra över en GIF och klicka ★ för att spara.</div>';
        return;
      }
      renderGifGrid(body, favs);
    }
    else if (tab === "upload") {
      body.innerHTML = [
        '<div class="gif-upload-area" id="gifUploadArea">',
          '<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>',
          '<div style="font-size:13px;font-weight:700;color:#94a3b8">Ladda upp GIF</div>',
          '<div class="gif-upload-hint">Klicka eller dra en .gif-fil hit (max 10 MB)</div>',
        '</div>',
        '<input type="file" id="gifUploadInput" accept=".gif,image/gif" style="display:none">'
      ].join("");

      var area = document.getElementById("gifUploadArea");
      var input = document.getElementById("gifUploadInput");

      area.addEventListener("click", function() { input.click(); });
      area.addEventListener("dragover", function(e) { e.preventDefault(); area.style.borderColor = "#3b82f6"; });
      area.addEventListener("dragleave", function() { area.style.borderColor = "#1e293b"; });
      area.addEventListener("drop", function(e) {
        e.preventDefault(); area.style.borderColor = "#1e293b";
        var file = e.dataTransfer.files[0];
        if (file) uploadGif(file);
      });
      input.addEventListener("change", function() {
        if (input.files[0]) uploadGif(input.files[0]);
      });
    }
  }

  function renderGifGrid(container, gifs) {
    var html = '<div class="gif-grid">';
    gifs.forEach(function(g) {
      var fav = isFav(g.url);
      html += '<div class="gif-grid-item" data-url="' + escAttr(g.url) + '" data-name="' + escAttr(g.name || "gif") + '">';
      html += '<img src="' + escAttr(g.url) + '" alt="GIF" loading="lazy">';
      html += '<button class="gif-fav-btn' + (fav ? ' is-fav' : '') + '" data-url="' + escAttr(g.url) + '" data-name="' + escAttr(g.name || "gif") + '" title="' + (fav ? 'Ta bort favorit' : 'Spara favorit') + '">★</button>';
      html += '</div>';
    });
    html += '</div>';
    container.innerHTML = html;

    // Click GIF to send
    container.querySelectorAll(".gif-grid-item").forEach(function(item) {
      item.addEventListener("click", function(e) {
        if (e.target.closest(".gif-fav-btn")) return;
        var url = item.dataset.url;
        var name = item.dataset.name;
        sendGif(url, name);
        closeAll();
      });
    });

    // Favorite button
    container.querySelectorAll(".gif-fav-btn").forEach(function(btn) {
      btn.addEventListener("click", function(e) {
        e.stopPropagation();
        var added = toggleFav(btn.dataset.url, btn.dataset.name);
        btn.classList.toggle("is-fav", added);
        btn.title = added ? "Ta bort favorit" : "Spara favorit";
      });
    });
  }

  // === Send GIF as image message ===
  function sendGif(url, name) {
    if (!url || typeof sendMessage !== "function") return;
    addRecent(url, name);

    // Build image payload and inject into composer
    var payload = JSON.stringify({
      type: "image",
      imageUrl: url,
      fileName: name || "gif.gif",
      mimeType: "image/gif",
      caption: ""
    });

    // We need to send this as a message directly
    var to = typeof getSelectedPeer === "function" ? getSelectedPeer() : "";
    if (!to) { alert("Välj en mottagare först."); return; }

    // Use the existing send flow — set msg value and trigger send
    var msgEl = document.getElementById("msg");
    if (msgEl) {
      // Temporarily store the GIF payload
      window._pendingGifPayload = payload;
      // Trigger send
      sendGifMessage(to, payload);
    }
  }

  async function sendGifMessage(to, gifPayload) {
    if (!to || !me || !connected) return;
    if (to !== currentPeer && typeof activateConvo === "function") await activateConvo(to);
    try {
      var payload;
      if (to === "runspacegpt") {
        payload = { to: to, text: gifPayload, iv: "", encryptedKey: "", senderEncryptedKey: "", recipientKeys: [], senderKeys: [], algorithm: "plain", encrypted: 0, replyToId: 0 };
      } else {
        var rd = await getRecipKeys(to);
        var sd = await getMyKeys();
        if (!sd || !sd.length) throw new Error("No sender keys");
        var enc = await encryptMsg(gifPayload, rd, sd);
        payload = { to: to, text: enc.text, iv: enc.iv, encryptedKey: enc.encryptedKey, senderEncryptedKey: enc.senderEncryptedKey, recipientKeys: enc.recipientKeys, senderKeys: enc.senderKeys, algorithm: enc.algorithm, encrypted: enc.encrypted, replyToId: 0 };
      }
      await connection.invoke("SendMessage", payload);
      addMsg(to, { id: null, from: me.username, text: gifPayload, ts: new Date().toISOString(), system: false, failed: false, replyTo: null });
    } catch(e) {
      console.error("GIF send error:", e);
      if (typeof addSysMsg === "function") addSysMsg(to, "Kunde inte skicka GIF.");
    }
  }

  // === Upload GIF ===
  async function uploadGif(file) {
    if (!file) return;
    if (!file.type.includes("gif") && !file.name.toLowerCase().endsWith(".gif")) {
      alert("Bara .gif-filer."); return;
    }
    if (file.size > 10 * 1024 * 1024) { alert("Max 10 MB."); return; }

    var area = document.getElementById("gifUploadArea");
    if (area) {
      area.innerHTML = '<div style="font-size:12px;color:#3b82f6;font-weight:700">Laddar upp...</div>';
    }

    try {
      var fd = new FormData();
      fd.append("image", file);
      var r = await fetch("/api/chat/upload-image", { method: "POST", credentials: "include", body: fd });
      var d = await r.json().catch(function() { return {}; });
      if (!r.ok) throw new Error(d.message || d.error || "Upload failed");
      var url = (d.imageUrl || "").trim();
      if (!url) throw new Error("No URL");

      // Add to recent
      addRecent(url, file.name || "uploaded.gif");

      // Switch to recent tab
      var panel = document.getElementById("gifPanel");
      if (panel) {
        panel.querySelectorAll(".gif-tab").forEach(function(t) { t.classList.remove("active"); });
        var recentTab = panel.querySelector('[data-tab="recent"]');
        if (recentTab) recentTab.classList.add("active");
        renderGifTab("recent");
      }
    } catch(e) {
      alert("Kunde inte ladda upp: " + e.message);
      renderGifTab("upload");
    }
  }

  function escAttr(s) {
    return String(s || "").replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/'/g, "&#39;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  // === Init when DOM ready ===
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    // Small delay to ensure other scripts have run
    setTimeout(init, 100);
  }
})();
