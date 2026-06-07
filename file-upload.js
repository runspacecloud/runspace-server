// === RunSpace File Upload Patch ===
// Overrides image-only upload to support all file types

(function() {
  // Override file select handler
  var _origHandleImgSelect = handleImgSelect;
  handleImgSelect = async function(file) {
    if (!file) return;
    if (file.size > 50 * 1024 * 1024) { alert("Max 50 MB."); return; }
    var isImg = file.type.startsWith("image/");
    if (isImg) {
      var reader = new FileReader();
      reader.onload = function() {
        var url = reader.result;
        var img = new Image();
        img.onload = function() {
          setPendingImage({ file: file, fileName: file.name, mimeType: file.type, size: file.size, previewUrl: url, width: img.naturalWidth, height: img.naturalHeight, isImage: true });
          updateComposer();
        };
        img.onerror = function() {
          setPendingImage({ file: file, fileName: file.name, mimeType: file.type, size: file.size, previewUrl: "", isImage: false });
          updateComposer();
        };
        img.src = url;
      };
      reader.readAsDataURL(file);
    } else {
      setPendingImage({ file: file, fileName: file.name, mimeType: file.type, size: file.size, previewUrl: "", isImage: false });
      updateComposer();
    }
  };

  // Override upload function to handle files
  var _origUploadImage = uploadImage;
  uploadImage = async function(file) {
    var isImg = file.type.startsWith("image/");
    if (isImg) {
      // Use original image endpoint
      return _origUploadImage(file);
    } else {
      // Use new file endpoint
      var fd = new FormData();
      fd.append("file", file);
      var r = await fetch("/api/chat/upload-file", { method: "POST", credentials: "include", body: fd });
      var d = await r.json().catch(function() { return {}; });
      if (!r.ok) throw new Error(d.error || "Upload failed");
      var url = String(d.fileUrl || "").trim();
      if (!url) throw new Error("No URL");
      return url;
    }
  };

  // Override setPendingImage to handle non-images
  var _origSetPending = setPendingImage;
  setPendingImage = function(d) {
    pendingImage = d;
    if (!d) {
      el.attachPreview.classList.remove("show");
      el.attachImg.src = "";
      el.attachInfo.textContent = "Ingen fil vald.";
      el.imageInput.value = "";
      return;
    }
    el.attachPreview.classList.add("show");
    if (d.previewUrl && d.isImage !== false) {
      el.attachImg.src = d.previewUrl;
      el.attachImg.style.display = "";
    } else {
      el.attachImg.src = "";
      el.attachImg.style.display = "none";
    }
    el.attachInfo.textContent = d.fileName + " \u00b7 " + fmtBytes(d.size) + (d.width && d.height ? " \u00b7 " + d.width + "\u00d7" + d.height : "");
  };

  // Override buildImgPayload to handle files
  var _origBuildImg = buildImgPayload;
  buildImgPayload = function(o) {
    if (o.type === "file") {
      return JSON.stringify({ type: "file", fileUrl: o.fileUrl, fileName: o.fileName || "file", mimeType: o.mimeType || "", size: o.size || 0, caption: o.caption || "" });
    }
    return _origBuildImg(o);
  };

  // Override tryParseImg to also parse file attachments
  var _origTryParse = tryParseImg;
  tryParseImg = function(t) {
    if (!t || typeof t !== "string") return null;
    try {
      var p = JSON.parse(t);
      if (p && p.type === "file" && p.fileUrl) {
        return { type: "file", fileUrl: p.fileUrl, fileName: String(p.fileName || "file"), mimeType: String(p.mimeType || ""), size: p.size || 0, caption: String(p.caption || "") };
      }
    } catch(e) {}
    return _origTryParse(t);
  };

  // Override renderBubble to show file downloads
  var _origRenderBubble = renderBubble;
  renderBubble = function(i) {
    var parsed = !i.system ? tryParseImg(i.text) : null;
    if (parsed && parsed.type === "file") {
      var ext = (parsed.fileName || "").split(".").pop().toLowerCase();
      var icon = getFileIcon(ext);
      var sizeStr = parsed.size ? fmtBytes(parsed.size) : "";
      var isGif = ext === "gif" && parsed.mimeType && parsed.mimeType.startsWith("image/");
      if (isGif) {
        return '<div class="msg-bubble"><div class="chat-image-wrap"><img class="chat-image" src="' + esc(parsed.fileUrl) + '" alt="' + esc(parsed.fileName) + '" style="max-width:300px;border-radius:8px">' + (parsed.caption ? '<div class="chat-image-caption">' + esc(parsed.caption) + '</div>' : '') + '</div></div>';
      }
      var isMedia = /^(mp4|webm|mov|avi)$/.test(ext);
      if (isMedia) {
        return '<div class="msg-bubble"><div style="margin-bottom:6px"><video controls preload="metadata" style="max-width:400px;max-height:300px;border-radius:8px"><source src="' + esc(parsed.fileUrl) + '"></video></div><div style="font-size:11px;color:var(--muted)">' + esc(parsed.fileName) + (sizeStr ? ' \u00b7 ' + sizeStr : '') + '</div>' + (parsed.caption ? '<div style="margin-top:4px">' + esc(parsed.caption) + '</div>' : '') + '</div>';
      }
      var isAudio = /^(mp3|wav|ogg|flac|m4a|aac)$/.test(ext);
      if (isAudio) {
        return '<div class="msg-bubble"><audio controls preload="metadata" style="max-width:300px"><source src="' + esc(parsed.fileUrl) + '"></audio><div style="font-size:11px;color:var(--muted);margin-top:4px">' + esc(parsed.fileName) + (sizeStr ? ' \u00b7 ' + sizeStr : '') + '</div></div>';
      }
      return '<div class="msg-bubble"><a href="' + esc(parsed.fileUrl) + '" download="' + esc(parsed.fileName) + '" target="_blank" style="display:flex;align-items:center;gap:10px;padding:8px 12px;background:var(--surface-3,#222d42);border-radius:8px;text-decoration:none;color:var(--text);border:1px solid var(--border)"><span style="font-size:28px;flex-shrink:0">' + icon + '</span><div style="flex:1;min-width:0"><div style="font-size:13px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">' + esc(parsed.fileName) + '</div><div style="font-size:11px;color:var(--muted)">' + sizeStr + ' \u00b7 ' + esc(ext.toUpperCase()) + '</div></div><span style="font-size:18px;color:var(--accent,#3b82f6);flex-shrink:0">\u2b07</span></a>' + (parsed.caption ? '<div style="margin-top:6px">' + esc(parsed.caption) + '</div>' : '') + '</div>';
    }
    return _origRenderBubble(i);
  };

  function getFileIcon(ext) {
    var icons = {
      pdf: "\ud83d\udcc4", doc: "\ud83d\udcc4", docx: "\ud83d\udcc4",
      xls: "\ud83d\udcca", xlsx: "\ud83d\udcca", csv: "\ud83d\udcca",
      ppt: "\ud83d\udcca", pptx: "\ud83d\udcca",
      zip: "\ud83d\udce6", rar: "\ud83d\udce6", "7z": "\ud83d\udce6", tar: "\ud83d\udce6", gz: "\ud83d\udce6",
      mp3: "\ud83c\udfb5", wav: "\ud83c\udfb5", ogg: "\ud83c\udfb5", flac: "\ud83c\udfb5", m4a: "\ud83c\udfb5", aac: "\ud83c\udfb5",
      mp4: "\ud83c\udfac", webm: "\ud83c\udfac", mov: "\ud83c\udfac", avi: "\ud83c\udfac",
      txt: "\ud83d\udcdd", md: "\ud83d\udcdd", json: "\ud83d\udcdd", xml: "\ud83d\udcdd",
      py: "\ud83d\udc0d", js: "\u2699\ufe0f", ts: "\u2699\ufe0f", html: "\ud83c\udf10", css: "\ud83c\udfa8",
      png: "\ud83d\uddbc\ufe0f", jpg: "\ud83d\uddbc\ufe0f", jpeg: "\ud83d\uddbc\ufe0f", gif: "\ud83d\uddbc\ufe0f", webp: "\ud83d\uddbc\ufe0f", svg: "\ud83d\uddbc\ufe0f"
    };
    return icons[ext] || "\ud83d\udcc1";
  }

  // Patch sendMessage to build file payload for non-images
  var _origSendMsg = sendMessage;
  sendMessage = async function() {
    if (currentView === 'group') {
      // Groups don't support file upload yet
      var gSend = typeof sendGroupMessage === 'function' ? sendGroupMessage : null;
      if (gSend) { await gSend(); return; }
    }
    var to = getSelectedPeer(), message = el.msg.value.trim();
    if (!canChat() || !to || (!message && !pendingImage) || currentView !== 'dm') return;
    if (message.length > MAX_MSG_LEN) return;
    if (to !== currentPeer) await activateConvo(to);
    var prevVal = el.msg.value, prevImg = pendingImage, prevReply = replyingTo;
    el.sendBtn.disabled = true; el.imageBtn.disabled = true;
    try {
      var plain = message;
      if (pendingImage) {
        var url = await uploadImage(pendingImage.file);
        if (pendingImage.isImage === false) {
          plain = buildImgPayload({ type: "file", fileUrl: url, fileName: pendingImage.fileName, mimeType: pendingImage.mimeType, size: pendingImage.size, caption: message });
        } else {
          plain = buildImgPayload({ imageUrl: url, fileName: pendingImage.fileName, mimeType: pendingImage.mimeType, caption: message });
        }
      }
      var rd = await getRecipKeys(to), sd = await getMyKeys();
      if (!sd || !sd.length) throw new Error("No sender keys");
      var enc = await encryptMsg(plain, rd, sd);
      var payload = { to: to, text: enc.text, iv: enc.iv, encryptedKey: enc.encryptedKey, senderEncryptedKey: enc.senderEncryptedKey, recipientKeys: enc.recipientKeys, senderKeys: enc.senderKeys, algorithm: enc.algorithm, encrypted: enc.encrypted, replyToId: (prevReply && prevReply.id) ? Number(prevReply.id) : 0 };
      await connection.invoke("SendMessage", payload);
      addMsg(to, { id: null, from: me.username, text: plain, ts: new Date().toISOString(), system: false, failed: false, replyTo: prevReply ? prevReply.id : null });
      el.msg.value = ""; setPendingImage(null); clearReply(); updateCharCount();
      if (currentPeer === to) el.msg.focus();
    } catch(e) {
      console.error(e);
      el.msg.value = prevVal;
      if (prevImg) setPendingImage(prevImg);
      if (prevReply) replyingTo = prevReply;
      var m = String(e && e.message || "");
      if (m.indexOf("RECIPIENT_NO_PUBLIC_KEY") >= 0) addSysMsg(to, "Anv\u00e4ndaren har inga chattnycklar.");
      else if (m.toLowerCase().indexOf("upload") >= 0) addSysMsg(to, "Filen kunde inte laddas upp.");
      else addSysMsg(to, "Kunde inte skicka.");
      updateCharCount();
    } finally { updateComposer(); }
  };
})();

// Ctrl+V paste clean v4
document.addEventListener("paste", async function(e){
  try {
    if (!e.clipboardData) return;

    var files = e.clipboardData.files || [];
    var items = e.clipboardData.items || [];
    var file = files && files.length ? files[0] : null;

    if (!file) {
      for (var i = 0; i < items.length; i++) {
        if (items[i].kind === "file") {
          file = items[i].getAsFile();
          break;
        }
      }
    }

    if (!file) return;

    e.preventDefault();

    if (typeof setPendingImage === "function") {
      var isImage = file.type && file.type.startsWith("image/");
      var url = isImage ? URL.createObjectURL(file) : "";

      setPendingImage({
        file: file,
        fileName: file.name || "pasted-file",
        mimeType: file.type || "",
        size: file.size || 0,
        previewUrl: url,
        isImage: isImage
      });
    }
  } catch (err) {
    console.error("Paste upload error:", err);
  }
});
