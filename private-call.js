// === RunSpace Private Calls v4 ===
var PrivateCall = (function() {
  var ICE = [
    { urls: "stun:stun.l.google.com:19302" },
    { urls: "stun:stun1.l.google.com:19302" },
    { urls: "stun:stun.cloudflare.com:3478" }
  ];
  var SPROFILES = {
    "4k30": { label: "4K 30fps (text & bild)", w: 3840, h: 2160, fps: 30, br: 20000000, hint: "detail" },
    "1440p144": { label: "1440p 144fps", w: 2560, h: 1440, fps: 144, br: 25000000, hint: "motion" },
    "1080p240": { label: "1080p 240fps", w: 1920, h: 1080, fps: 240, br: 20000000, hint: "motion" }
  };
  var SQK = "runspace_call_sq";
  var state = "idle", callPeer = null, localStream = null, screenStream = null;
  var pc = null, remoteAudio = null, isMuted = false, isDeafened = false, isScreenSharing = false;
  var sq = localStorage.getItem(SQK) || "4k30";
  var callStart = null, durInt = null, ringInt = null, actx = null;

  function sp() { return SPROFILES[sq] || SPROFILES["4k30"]; }

  function playRing() {
    stopRing();
    try {
      actx = new (window.AudioContext || window.webkitAudioContext)();
      var r = function() {
        if (!actx || state === "idle" || state === "active") return;
        var o = actx.createOscillator(), g = actx.createGain();
        o.connect(g); g.connect(actx.destination);
        o.type = "sine"; o.frequency.value = 440;
        g.gain.setValueAtTime(0.15, actx.currentTime);
        g.gain.exponentialRampToValueAtTime(0.001, actx.currentTime + 0.8);
        o.start(); o.stop(actx.currentTime + 0.8);
      };
      r(); ringInt = setInterval(r, 2000);
    } catch(e) {}
  }
  function stopRing() {
    if (ringInt) { clearInterval(ringInt); ringInt = null; }
    if (actx && state !== "active") { try { actx.close(); } catch(e) {} actx = null; }
  }

  function getOvl() {
    var el = document.getElementById("callOverlay");
    if (el) return el;
    el = document.createElement("div");
    el.id = "callOverlay";
    el.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.75);display:none;align-items:center;justify-content:center;z-index:300;backdrop-filter:blur(8px);flex-direction:column";

    // Video area
    var va = document.createElement("div"); va.id = "callVideoArea";
    va.style.cssText = "display:none;position:fixed;inset:0;z-index:310;background:#000";
    var vid = document.createElement("video"); vid.id = "callRemoteVideo";
    vid.autoplay = true; vid.playsInline = true;
    vid.style.cssText = "width:100%;height:100%;object-fit:contain;cursor:pointer";
    var ctrl = document.createElement("div"); ctrl.id = "callVideoCtrl";
    ctrl.style.cssText = "position:fixed;bottom:0;left:0;right:0;background:linear-gradient(transparent,rgba(0,0,0,0.85));padding:16px 20px 20px;display:flex;align-items:center;justify-content:space-between;z-index:311;opacity:1;transition:opacity 0.3s";
    var lbl = document.createElement("div"); lbl.id = "callVidLabel";
    lbl.style.cssText = "font-size:13px;font-weight:700;color:#fff";
    var cbtns = document.createElement("div");
    cbtns.style.cssText = "display:flex;gap:8px;align-items:center";
    var mb = document.createElement("button"); mb.textContent = "Minimera";
    mb.style.cssText = "border:none;border-radius:8px;padding:8px 16px;background:rgba(255,255,255,0.15);color:#fff;font-weight:700;font-size:12px;cursor:pointer";
    mb.onclick = function() { minVid(); };
    var hb = document.createElement("button"); hb.textContent = "L\u00e4gg p\u00e5";
    hb.style.cssText = "border:none;border-radius:8px;padding:8px 16px;background:#ef4444;color:#fff;font-weight:700;font-size:12px;cursor:pointer";
    hb.onclick = function() { hangup(); };
    cbtns.appendChild(mb); cbtns.appendChild(hb);
    ctrl.appendChild(lbl); ctrl.appendChild(cbtns);
    va.appendChild(vid); va.appendChild(ctrl);

    var ht = null;
    va.addEventListener("mousemove", function() { ctrl.style.opacity = "1"; clearTimeout(ht); ht = setTimeout(function() { ctrl.style.opacity = "0"; }, 3000); });
    va.addEventListener("mouseleave", function() { ctrl.style.opacity = "1"; });
    vid.addEventListener("click", function() { ctrl.style.opacity = ctrl.style.opacity === "0" ? "1" : "0"; });
    vid.addEventListener("dblclick", function(e) { e.preventDefault(); if (document.fullscreenElement) document.exitFullscreen(); else if (vid.requestFullscreen) vid.requestFullscreen(); });
    document.addEventListener("keydown", function(e) { if (e.key === "Escape" && va.style.display !== "none") minVid(); });

    // Call box
    var box = document.createElement("div"); box.id = "callBox";
    box.style.cssText = "background:var(--surface,#111827);border:1px solid var(--border,#1e293b);border-radius:16px;padding:24px 28px;min-width:320px;text-align:center;box-shadow:0 20px 60px rgba(0,0,0,0.5)";
    box.innerHTML = '<div id="callAv" style="width:56px;height:56px;border-radius:50%;background:var(--surface-2,#1a2236);margin:0 auto 12px;display:flex;align-items:center;justify-content:center;font-size:22px;font-weight:800;color:var(--dim,#94a3b8)">?</div><div id="callTi" style="font-size:16px;font-weight:800;margin-bottom:4px">Samtal</div><div id="callSt" style="font-size:12px;color:var(--muted,#64748b);margin-bottom:16px">...</div><div id="callDur" style="font-size:13px;font-weight:700;color:var(--accent,#3b82f6);margin-bottom:12px;display:none">00:00</div><div id="callBtns" style="display:flex;gap:8px;justify-content:center;flex-wrap:wrap"></div><div id="callQP" style="display:none;margin-top:12px"></div>';

    el.appendChild(va); el.appendChild(box);
    document.body.appendChild(el);
    return el;
  }

  function showVidFS() {
    var a = document.getElementById("callVideoArea");
    var l = document.getElementById("callVidLabel");
    if (!a) return; a.style.display = "block";
    if (l) l.textContent = (callPeer || "?") + " delar sk\u00e4rm";
    var b = document.getElementById("callBox"); if (b) b.style.display = "none";
  }
  function minVid() {
    if (document.fullscreenElement) document.exitFullscreen();
    var a = document.getElementById("callVideoArea"); if (a) a.style.display = "none";
    var b = document.getElementById("callBox"); if (b) b.style.display = "";
  }

  function showOvl(name, status, btns) {
    var el = getOvl(); el.style.display = "flex";
    document.getElementById("callAv").textContent = (name || "?")[0].toUpperCase();
    document.getElementById("callTi").textContent = name || "Samtal";
    document.getElementById("callSt").textContent = status;
    document.getElementById("callBtns").innerHTML = btns;
    document.getElementById("callDur").style.display = "none";
    document.getElementById("callVideoArea").style.display = "none";
    document.getElementById("callBox").style.display = "";
    var qp = document.getElementById("callQP"); if (qp) qp.style.display = "none";
  }
  function hideOvl() {
    var el = document.getElementById("callOverlay"); if (el) el.style.display = "none";
    minVid(); stopDur();
  }

  function updBtns() {
    var b = "";
    b += bh(isMuted ? "\ud83d\udd07" : "\ud83c\udf99\ufe0f", isMuted ? "act" : "g", "PrivateCall.toggleMute()", "Mikrofon");
    b += bh(isDeafened ? "\ud83d\udd15" : "\ud83d\udd0a", isDeafened ? "act" : "g", "PrivateCall.toggleDeafen()", "Ljud");
    b += bh(isScreenSharing ? "\ud83d\udfe2" : "\ud83d\udda5\ufe0f", isScreenSharing ? "scr" : "g", "PrivateCall.toggleScreen()", "Sk\u00e4rm");
    b += bh("\ud83d\udcde", "r", "PrivateCall.hangup()", "L\u00e4gg p\u00e5");
    document.getElementById("callBtns").innerHTML = b;
    var qp = document.getElementById("callQP"); if (qp) qp.style.display = "none";
  }

  function showQP() {
    var qp = document.getElementById("callQP");
    if (!qp) return; qp.style.display = ""; qp.innerHTML = "";
    var t = document.createElement("div");
    t.style.cssText = "font-size:12px;font-weight:700;color:var(--text,#e2e8f0);margin-bottom:8px";
    t.textContent = "V\u00e4lj kvalitet"; qp.appendChild(t);
    Object.keys(SPROFILES).forEach(function(k) {
      var p = SPROFILES[k], btn = document.createElement("button");
      btn.textContent = p.label;
      btn.style.cssText = "display:block;width:100%;text-align:left;border:1px solid var(--border,#1e293b);border-radius:8px;padding:10px 12px;margin-bottom:6px;background:var(--surface-2,#1a2236);color:var(--text,#e2e8f0);font-size:13px;font-weight:600;cursor:pointer";
      btn.addEventListener("click", function() { sq = k; localStorage.setItem(SQK, k); qp.style.display = "none"; startSS(); });
      qp.appendChild(btn);
    });
    var c = document.createElement("button"); c.textContent = "Avbryt";
    c.style.cssText = "display:block;width:100%;text-align:center;border:none;padding:8px;color:var(--muted,#64748b);font-size:12px;cursor:pointer;background:none";
    c.addEventListener("click", function() { qp.style.display = "none"; });
    qp.appendChild(c);
  }

  function startDur() {
    callStart = Date.now();
    var d = document.getElementById("callDur"); if (d) d.style.display = "";
    durInt = setInterval(function() {
      var s = Math.floor((Date.now() - callStart) / 1000);
      if (d) d.textContent = String(Math.floor(s / 60)).padStart(2, "0") + ":" + String(s % 60).padStart(2, "0");
    }, 1000);
  }
  function stopDur() { if (durInt) { clearInterval(durInt); durInt = null; } }

  function bh(icon, col, oc, ti) {
    var bg, tc;
    if (col === "r") { bg = "#ef4444"; tc = "#fff"; }
    else if (col === "gr") { bg = "#22c55e"; tc = "#fff"; }
    else if (col === "act") { bg = "rgba(239,68,68,0.15)"; tc = "#ef4444"; }
    else if (col === "scr") { bg = "rgba(34,197,94,0.15)"; tc = "#22c55e"; }
    else { bg = "var(--surface-2,#1a2236)"; tc = "var(--dim,#94a3b8)"; }
    return '<button onclick="' + oc + '" title="' + (ti || "") + '" style="border:none;border-radius:10px;width:44px;height:44px;font-size:18px;cursor:pointer;background:' + bg + ';color:' + tc + ';display:flex;align-items:center;justify-content:center">' + icon + '</button>';
  }

  // === Screen share ===
  async function startSS() {
    if (!pc || state !== "active" || isScreenSharing) return;
    var p = sp();
    try {
      screenStream = await navigator.mediaDevices.getDisplayMedia({
        video: { width: { ideal: p.w, max: 3840 }, height: { ideal: p.h, max: 2160 }, frameRate: { ideal: p.fps, max: 240 }, displaySurface: "monitor", cursor: "always" },
        audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false, sampleRate: 48000 }
      });
      isScreenSharing = true;
      var vt = screenStream.getVideoTracks()[0];
      if (vt) {
        if (typeof vt.contentHint !== "undefined") vt.contentHint = p.hint;
        vt.onended = function() { stopSS(); };
      }
      screenStream.getTracks().forEach(function(t) { pc.addTrack(t, screenStream); });
      await reneg();
      setTimeout(function() {
        if (!pc) return;
        pc.getSenders().forEach(function(s) {
          if (s.track && s.track.kind === "video") {
            var par = s.getParameters();
            if (!par.encodings) par.encodings = [{}];
            if (!par.encodings.length) par.encodings.push({});
            par.encodings[0].maxBitrate = p.br;
            par.encodings[0].maxFramerate = p.fps;
            par.encodings[0].scaleResolutionDownBy = 1.0;
            par.encodings[0].priority = "high";
            par.encodings[0].networkPriority = "high";
            s.setParameters(par).catch(function() {});
          }
        });
      }, 300);
      updBtns();
    } catch(e) {
      isScreenSharing = false; screenStream = null;
      if (e.name !== "NotAllowedError") console.error("SS fail:", e);
    }
  }
  function stopSS() {
    if (!isScreenSharing || !pc) return;
    if (screenStream) {
      pc.getSenders().forEach(function(s) {
        if (s.track && screenStream.getTracks().some(function(t) { return t.id === s.track.id; })) { try { pc.removeTrack(s); } catch(e) {} }
      });
      screenStream.getTracks().forEach(function(t) { t.stop(); }); screenStream = null;
    }
    isScreenSharing = false; reneg(); updBtns();
  }
  function toggleScreen() { if (isScreenSharing) { stopSS(); return; } showQP(); }

  async function reneg() {
    if (!pc || !callPeer) return;
    try { var o = await pc.createOffer(); await pc.setLocalDescription(o); await connection.invoke("SendCallOffer", callPeer, pc.localDescription.sdp); } catch(e) { console.error("Reneg fail:", e); }
  }

  // === Call flow ===
  async function call(u) {
    if (state !== "idle" || !u) return;
    callPeer = u; state = "calling";
    showOvl(u, "Ringer...", bh("\ud83d\udcde", "r", "PrivateCall.hangup()", "Avbryt"));
    playRing();
    try { await connection.invoke("CallUser", u); } catch(e) { console.error(e); cleanup(); alert("Kunde inte ringa."); }
    setTimeout(function() { if (state === "calling") { hangup(); showOvl(callPeer || u, "Inget svar", ""); setTimeout(hideOvl, 3000); } }, 30000);
  }
  function onIncoming(from) {
    if (state !== "idle") { try { connection.invoke("RejectCall", from); } catch(e) {} return; }
    state = "ringing"; callPeer = from; playRing();
    showOvl(from, "Ringer dig...", bh("\ud83d\udcde", "gr", "PrivateCall.accept()", "Svara") + bh("\ud83d\udcde", "r", "PrivateCall.reject()", "Avvisa"));
  }
  async function accept() {
    if (state !== "ringing" || !callPeer) return;
    stopRing(); state = "active";
    document.getElementById("callSt").textContent = "Ansluter..."; updBtns();
    try { localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false }); setupPC(false); await connection.invoke("AcceptCall", callPeer); }
    catch(e) { console.error(e); cleanup(); alert("Kunde inte svara."); }
  }
  function reject() { if (!callPeer) return; stopRing(); try { connection.invoke("RejectCall", callPeer); } catch(e) {} cleanup(); }
  function onAccepted(by) {
    if (state !== "calling") return; stopRing(); state = "active";
    document.getElementById("callSt").textContent = "Ansluter..."; updBtns();
    (async function() { try { localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false }); setupPC(true); } catch(e) { console.error(e); hangup(); } })();
  }
  function onRejected(by) { stopRing(); showOvl(by, "Avvisade samtalet", ""); setTimeout(function() { hideOvl(); cleanup(); }, 3000); }
  function onEnded(by) { stopRing(); stopSS(); minVid(); showOvl(by || callPeer || "Samtal", "Samtalet avslutades", ""); setTimeout(function() { hideOvl(); cleanup(); }, 2000); }
  function hangup() { stopRing(); stopSS(); minVid(); if (callPeer) { try { connection.invoke("EndCall", callPeer); } catch(e) {} } hideOvl(); cleanup(); }
  function toggleMute() { isMuted = !isMuted; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = !isMuted; }); updBtns(); }
  function toggleDeafen() {
    isDeafened = !isDeafened; if (remoteAudio) remoteAudio.muted = isDeafened;
    if (isDeafened && !isMuted) { isMuted = true; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = false; }); }
    else if (!isDeafened && isMuted) { isMuted = false; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = true; }); }
    updBtns();
  }

  // === WebRTC ===
  function setupPC(init) {
    pc = new RTCPeerConnection({ iceServers: ICE, sdpSemantics: "unified-plan" });
    remoteAudio = new Audio(); remoteAudio.autoplay = true;
    if (localStream) localStream.getTracks().forEach(function(t) { pc.addTrack(t, localStream); });

    pc.ontrack = function(ev) {
      // Low latency
      try { if (ev.receiver) { if (ev.receiver.playoutDelayHint !== undefined) ev.receiver.playoutDelayHint = 0; if (ev.receiver.jitterBufferTarget !== undefined) ev.receiver.jitterBufferTarget = 0; } } catch(e) {}
      if (ev.track.kind === "audio" && ev.streams[0]) {
        remoteAudio.srcObject = ev.streams[0];
        document.getElementById("callSt").textContent = "I samtal";
        startDur(); collapse();
      } else if (ev.track.kind === "video" && ev.streams[0]) {
        var v = document.getElementById("callRemoteVideo");
        if (v) { v.srcObject = ev.streams[0]; showVidFS(); }
        ev.track.onended = function() { minVid(); if (v) v.srcObject = null; };
      }
    };
    pc.onicecandidate = function(ev) { if (ev.candidate && callPeer) { try { connection.invoke("SendCallIce", callPeer, JSON.stringify(ev.candidate)).catch(function() {}); } catch(e) {} } };
    pc.onconnectionstatechange = function() {
      if (pc.connectionState === "failed") pc.restartIce();
      if (pc.connectionState === "disconnected") setTimeout(function() { if (pc && pc.connectionState === "disconnected") onEnded(callPeer); }, 3000);
    };
    pc.onnegotiationneeded = function() {
      if (pc._neg) return; pc._neg = true;
      setTimeout(function() { pc._neg = false; if (init || pc.signalingState === "stable") reneg(); }, 200);
    };
    if (init) {
      pc.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: true, voiceActivityDetection: false })
        .then(function(o) { return pc.setLocalDescription(o); })
        .then(function() { return connection.invoke("SendCallOffer", callPeer, pc.localDescription.sdp); })
        .catch(function(e) { console.error("Offer fail:", e); });
    }
  }

  async function onOffer(from, sdp) {
    if (!pc || !localStream) return;
    try { await pc.setRemoteDescription(new RTCSessionDescription({ type: "offer", sdp: sdp })); var a = await pc.createAnswer(); await pc.setLocalDescription(a); await connection.invoke("SendCallAnswer", from, a.sdp); } catch(e) { console.error(e); }
  }
  async function onAnswer(from, sdp) { if (!pc) return; try { await pc.setRemoteDescription(new RTCSessionDescription({ type: "answer", sdp: sdp })); } catch(e) {} }
  async function onIce(from, j) { if (!pc) return; try { await pc.addIceCandidate(new RTCIceCandidate(JSON.parse(j))); } catch(e) {} }

  // === Collapse/Expand ===
  function collapse() {
    var el = document.getElementById("callOverlay"); if (!el) return;
    el.style.cssText = "position:fixed;top:0;left:0;right:0;height:48px;background:var(--surface,#111827);border-bottom:1px solid var(--border,#1e293b);display:flex;align-items:center;justify-content:space-between;z-index:300;padding:0 16px;flex-direction:row";
    var box = document.getElementById("callBox"); if (box) box.style.cssText = "display:flex;align-items:center;gap:12px;padding:0;background:none;border:none;box-shadow:none;min-width:auto;text-align:left;border-radius:0";
    var av = document.getElementById("callAv"); if (av) av.style.cssText = "width:28px;height:28px;border-radius:50%;background:var(--surface-2);display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:800;color:var(--dim);flex-shrink:0";
    var ti = document.getElementById("callTi"); if (ti) ti.style.cssText = "font-size:13px;font-weight:700;margin:0";
    var st = document.getElementById("callSt"); if (st) st.style.display = "none";
    var dur = document.getElementById("callDur"); if (dur) dur.style.cssText = "font-size:12px;font-weight:700;color:var(--accent,#3b82f6);margin:0";
    var btns = document.getElementById("callBtns"); if (btns) btns.style.cssText = "display:flex;gap:6px;align-items:center";
    var qp = document.getElementById("callQP"); if (qp) qp.style.display = "none";
  }
  function expand() {
    var el = document.getElementById("callOverlay"); if (!el) return;
    el.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.75);display:flex;align-items:center;justify-content:center;z-index:300;backdrop-filter:blur(8px);flex-direction:column";
    var box = document.getElementById("callBox"); if (box) box.style.cssText = "background:var(--surface,#111827);border:1px solid var(--border,#1e293b);border-radius:16px;padding:24px 28px;min-width:320px;text-align:center;box-shadow:0 20px 60px rgba(0,0,0,0.5)";
    var av = document.getElementById("callAv"); if (av) av.style.cssText = "width:56px;height:56px;border-radius:50%;background:var(--surface-2);margin:0 auto 12px;display:flex;align-items:center;justify-content:center;font-size:22px;font-weight:800;color:var(--dim)";
    var ti = document.getElementById("callTi"); if (ti) ti.style.cssText = "font-size:16px;font-weight:800;margin-bottom:4px";
    var st = document.getElementById("callSt"); if (st) st.style.cssText = "font-size:12px;color:var(--muted);margin-bottom:16px";
    var dur = document.getElementById("callDur"); if (dur) dur.style.cssText = "font-size:13px;font-weight:700;color:var(--accent);margin-bottom:12px";
    var btns = document.getElementById("callBtns"); if (btns) btns.style.cssText = "display:flex;gap:8px;justify-content:center;flex-wrap:wrap";
  }

  function cleanup() {
    state = "idle"; callPeer = null; isMuted = false; isDeafened = false; isScreenSharing = false;
    stopRing(); stopDur();
    if (screenStream) { screenStream.getTracks().forEach(function(t) { t.stop(); }); screenStream = null; }
    if (pc) { try { pc.close(); } catch(e) {} pc = null; }
    if (localStream) { localStream.getTracks().forEach(function(t) { t.stop(); }); localStream = null; }
    if (remoteAudio) { remoteAudio.pause(); remoteAudio.srcObject = null; remoteAudio = null; }
    var v = document.getElementById("callRemoteVideo"); if (v) v.srcObject = null;
  }

  function bind() {
    if (typeof connection === "undefined") return;
    connection.on("IncomingCall", onIncoming);
    connection.on("CallAccepted", onAccepted);
    connection.on("CallRejected", onRejected);
    connection.on("CallEnded", onEnded);
    connection.on("CallOffer", onOffer);
    connection.on("CallAnswer", onAnswer);
    connection.on("CallIce", onIce);
  }

  return {
    call: call, accept: accept, reject: reject, hangup: hangup,
    toggleMute: toggleMute, toggleDeafen: toggleDeafen,
    toggleScreen: toggleScreen, startShare: startSS, bind: bind,
    hideOverlay: hideOvl, expandOverlay: expand, collapseToBar: collapse,
    getState: function() { return { state: state, peer: callPeer, isMuted: isMuted, isDeafened: isDeafened, isScreenSharing: isScreenSharing, screenQuality: sq }; }
  };
})();

if (typeof connection !== "undefined") PrivateCall.bind();
