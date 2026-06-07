// === RunSpace Group Voice (WebRTC Mesh) ===
// Requires: groups.js loaded first, signalR connection available

var GroupVoice = (function() {
  var ICE_SERVERS = [
    { urls: "stun:stun.l.google.com:19302" },
    { urls: "stun:stun1.l.google.com:19302" },
    { urls: "stun:stun.cloudflare.com:3478" }
  ];

  var MAX_USERS = 6;
  var QUALITY_KEY = "runspace_gv_quality";

  var PROFILES = {
    ultra: { label: "Ultra (510kbps stereo)", sampleRate: 48000, channelCount: 2, echoCancellation: false, noiseSuppression: false, autoGainControl: false, maxBitrate: 510000, stereo: true, dtx: false, fec: true, ptime: 10 },
    high: { label: "H\u00f6g (256kbps stereo)", sampleRate: 48000, channelCount: 2, echoCancellation: false, noiseSuppression: false, autoGainControl: false, maxBitrate: 256000, stereo: true, dtx: false, fec: true, ptime: 10 },
    balanced: { label: "Balanserad (128kbps)", sampleRate: 48000, channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true, maxBitrate: 128000, stereo: false, dtx: false, fec: true, ptime: 20 },
    low: { label: "L\u00e5g (64kbps)", sampleRate: 48000, channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true, maxBitrate: 64000, stereo: false, dtx: true, fec: true, ptime: 20 }
  };

  var localStream = null;
  var isMuted = false;
  var isDeafened = false;
  var isSpeaking = false;
  var currentQuality = localStorage.getItem(QUALITY_KEY) || "balanced";
  var audioCtx = null;
  var analyser = null;
  var speakingInterval = null;

  // peerId -> { pc, audio, speaking }
  var peers = new Map();

  function profile() { return PROFILES[currentQuality] || PROFILES.balanced; }

  // == Mic ==
  async function getMicStream() {
    var p = profile();
    return navigator.mediaDevices.getUserMedia({
      audio: {
        sampleRate: { ideal: p.sampleRate },
        channelCount: { ideal: p.channelCount },
        echoCancellation: { ideal: p.echoCancellation },
        noiseSuppression: { ideal: p.noiseSuppression },
        autoGainControl: { ideal: p.autoGainControl },
        latency: { ideal: 0.01 }
      }, video: false
    });
  }

  // == Speaking detection ==
  function setupSpeaking() {
    if (!localStream) return;
    try {
      audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
      var src = audioCtx.createMediaStreamSource(localStream);
      analyser = audioCtx.createAnalyser();
      analyser.fftSize = 512;
      analyser.smoothingTimeConstant = 0.35;
      src.connect(analyser);
      var buf = new Uint8Array(analyser.frequencyBinCount);
      speakingInterval = setInterval(function() {
        if (!analyser || isMuted) {
          if (isSpeaking) { isSpeaking = false; updateSpeakingUI(); }
          return;
        }
        analyser.getByteFrequencyData(buf);
        var sum = 0; for (var i = 0; i < buf.length; i++) sum += buf[i];
        var was = isSpeaking;
        isSpeaking = (sum / buf.length) > 12;
        if (was !== isSpeaking) updateSpeakingUI();
      }, 80);
    } catch(e) { console.error("Speaking detect fail:", e); }
  }

  function updateSpeakingUI() {
    // Update local user's speaking state in groupVoiceUsers
    if (myVoiceChannel && activeGroup) {
      var key = activeGroup.groupId + ':' + myVoiceChannel;
      var users = groupVoiceUsers[key];
      if (users) {
        var idx = users.indexOf(me.username);
        if (idx >= 0) {
          // Store speaking state separately
          if (!GroupVoice._speakingUsers) GroupVoice._speakingUsers = {};
          GroupVoice._speakingUsers[me.username] = isSpeaking;
        }
      }
    }
    renderGroupSidebar();
  }

  // == SDP Enhancement ==
  function enhanceSDP(sdp) {
    var p = profile();
    var m = sdp.match(/a=rtpmap:(\d+) opus\/48000\/2/);
    if (!m) return sdp;
    var pt = m[1];
    var re = new RegExp("a=fmtp:" + pt + " [^\\r\\n]+", "g");
    sdp = sdp.replace(re, "");
    var fmtp = "a=fmtp:" + pt +
      " minptime=" + p.ptime +
      ";useinbandfec=" + (p.fec ? "1" : "0") +
      ";usedtx=" + (p.dtx ? "1" : "0") +
      ";stereo=" + (p.stereo ? "1" : "0") +
      ";sprop-stereo=" + (p.stereo ? "1" : "0") +
      ";maxaveragebitrate=" + p.maxBitrate +
      ";maxplaybackrate=" + p.sampleRate +
      ";cbr=1";
    sdp = sdp.replace(m[0], m[0] + "\r\n" + fmtp);
    sdp = sdp.replace(/m=audio (\d+) ([A-Z/]+) ([\d ]+)/, function(match, port, proto, payloads) {
      var pts = payloads.split(" ");
      var idx = pts.indexOf(pt);
      if (idx > 0) { pts.splice(idx, 1); pts.unshift(pt); }
      return "m=audio " + port + " " + proto + " " + pts.join(" ");
    });
    return sdp;
  }

  function applyBitrate(pc) {
    var p = profile();
    pc.getSenders().forEach(function(s) {
      if (!s.track || s.track.kind !== "audio") return;
      var params = s.getParameters();
      if (!params.encodings) params.encodings = [{}];
      if (!params.encodings.length) params.encodings.push({});
      params.encodings[0].maxBitrate = p.maxBitrate;
      s.setParameters(params).catch(function() {});
    });
  }

  // == Peer connections ==
  function createPeer(remoteUser, isInitiator) {
    if (peers.has(remoteUser)) return peers.get(remoteUser);

    var pc = new RTCPeerConnection({ iceServers: ICE_SERVERS, sdpSemantics: "unified-plan" });
    var audio = new Audio();
    audio.autoplay = true;
    audio.muted = isDeafened;

    var peerData = { pc: pc, audio: audio, speaking: false };
    peers.set(remoteUser, peerData);

    if (localStream) {
      localStream.getTracks().forEach(function(t) { pc.addTrack(t, localStream); });
    }

    pc.ontrack = function(ev) {
      if (ev.track.kind === "audio" && ev.streams[0]) audio.srcObject = ev.streams[0];
    };

    pc.onicecandidate = function(ev) {
      if (ev.candidate) {
        try { connection.invoke("SendGroupVoiceIce", remoteUser, JSON.stringify(ev.candidate)).catch(function() {}); } catch(e) {}
      }
    };

    pc.onconnectionstatechange = function() {
      if (pc.connectionState === "connected") applyBitrate(pc);
      if (pc.connectionState === "failed") pc.restartIce();
    };

    if (isInitiator) {
      pc.createOffer({ offerToReceiveAudio: true, voiceActivityDetection: false })
        .then(function(offer) {
          offer.sdp = enhanceSDP(offer.sdp);
          return pc.setLocalDescription(offer);
        })
        .then(function() {
          return connection.invoke("SendGroupVoiceOffer", remoteUser, pc.localDescription.sdp);
        })
        .catch(function(e) { console.error("GV offer fail:", e); });
    }

    return peerData;
  }

  function destroyPeer(username) {
    if (!peers.has(username)) return;
    var p = peers.get(username);
    if (p.pc) p.pc.close();
    if (p.audio) { p.audio.pause(); p.audio.srcObject = null; }
    peers.delete(username);
  }

  function destroyAll() {
    peers.forEach(function(p, u) { destroyPeer(u); });
    peers.clear();
  }

  // == SignalR handlers ==
  function bindSignalR() {
    connection.on("GroupVoiceOffer", async function(fromUser, sdp) {
      if (!myVoiceChannel || !localStream) return;
      var peer = createPeer(fromUser, false);
      try {
        await peer.pc.setRemoteDescription(new RTCSessionDescription({ type: "offer", sdp: sdp }));
        var answer = await peer.pc.createAnswer();
        answer.sdp = enhanceSDP(answer.sdp);
        await peer.pc.setLocalDescription(answer);
        await connection.invoke("SendGroupVoiceAnswer", fromUser, answer.sdp);
        setTimeout(function() { applyBitrate(peer.pc); }, 500);
      } catch(e) { console.error("GV handle offer fail:", e); }
    });

    connection.on("GroupVoiceAnswer", async function(fromUser, sdp) {
      var peer = peers.get(fromUser);
      if (!peer) return;
      try {
        await peer.pc.setRemoteDescription(new RTCSessionDescription({ type: "answer", sdp: sdp }));
        setTimeout(function() { applyBitrate(peer.pc); }, 500);
      } catch(e) { console.error("GV handle answer fail:", e); }
    });

    connection.on("GroupVoiceIce", async function(fromUser, json) {
      var peer = peers.get(fromUser);
      if (!peer) return;
      try { await peer.pc.addIceCandidate(new RTCIceCandidate(JSON.parse(json))); } catch(e) {}
    });
  }

  // == Public API ==
  async function join(groupId, channelId) {
    if (myVoiceChannel) await leave();
    try {
      localStream = await getMicStream();
      await connection.invoke("JoinGroupVoice", groupId, channelId);
      myVoiceChannel = channelId;

      var key = groupId + ':' + channelId;
      if (!groupVoiceUsers[key]) groupVoiceUsers[key] = [];
      if (groupVoiceUsers[key].indexOf(me.username) === -1) groupVoiceUsers[key].push(me.username);

      setupSpeaking();

      // Create peer connections to existing users in channel
      var usersInChannel = groupVoiceUsers[key].filter(function(u) { return u !== me.username; });
      usersInChannel.forEach(function(u) { createPeer(u, true); });

      renderGroupSidebar();
    } catch(e) {
      console.error("GV join fail:", e);
      cleanup();
      if (e.name === "NotAllowedError") alert("Mikrofontillg\u00e5ng nekad.");
      else if (e.name === "NotFoundError") alert("Ingen mikrofon hittad.");
      else alert("Kunde inte g\u00e5 med i r\u00f6stkanalen.");
    }
  }

  async function leave() {
    if (!myVoiceChannel || !activeGroup) return;
    try { await connection.invoke("LeaveGroupVoice", activeGroup.groupId, myVoiceChannel); } catch(e) {}

    var key = activeGroup.groupId + ':' + myVoiceChannel;
    if (groupVoiceUsers[key]) {
      groupVoiceUsers[key] = groupVoiceUsers[key].filter(function(u) { return u !== me.username; });
    }

    cleanup();
    renderGroupSidebar();
  }

  function toggleMute() {
    isMuted = !isMuted;
    if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = !isMuted; });
    renderGroupSidebar();
  }

  function toggleDeafen() {
    isDeafened = !isDeafened;
    peers.forEach(function(p) { if (p.audio) p.audio.muted = isDeafened; });
    if (isDeafened && !isMuted) { isMuted = true; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = false; }); }
    else if (!isDeafened && isMuted) { isMuted = false; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = true; }); }
    renderGroupSidebar();
  }

  function setQuality(q) {
    if (!PROFILES[q]) return;
    currentQuality = q;
    localStorage.setItem(QUALITY_KEY, q);
    peers.forEach(function(p) { applyBitrate(p.pc); });
    renderGroupSidebar();
  }

  function cleanup() {
    if (speakingInterval) { clearInterval(speakingInterval); speakingInterval = null; }
    if (audioCtx) { try { audioCtx.close(); } catch(e) {} audioCtx = null; analyser = null; }
    destroyAll();
    if (localStream) { localStream.getTracks().forEach(function(t) { t.stop(); }); localStream = null; }
    isSpeaking = false; isMuted = false; isDeafened = false; myVoiceChannel = null;
    if (GroupVoice._speakingUsers) GroupVoice._speakingUsers = {};
  }

  function isUserSpeaking(username) {
    if (username === me.username) return isSpeaking;
    var peer = peers.get(username);
    return peer ? peer.speaking : false;
  }

  function getState() {
    return { active: !!myVoiceChannel, channelId: myVoiceChannel, isMuted: isMuted, isDeafened: isDeafened, isSpeaking: isSpeaking, quality: currentQuality, qualityLabel: profile().label, peers: Array.from(peers.keys()) };
  }

  function getQualityOptions() {
    return Object.entries(PROFILES).map(function(e) {
      return { id: e[0], label: e[1].label, active: e[0] === currentQuality };
    });
  }

  return {
    join: join, leave: leave, toggleMute: toggleMute, toggleDeafen: toggleDeafen,
    setQuality: setQuality, getState: getState, getQualityOptions: getQualityOptions,
    cleanup: cleanup, bindSignalR: bindSignalR, isUserSpeaking: isUserSpeaking,
    _speakingUsers: {}
  };
})();

// Bind SignalR handlers when connection exists
if (typeof connection !== 'undefined') GroupVoice.bindSignalR();
