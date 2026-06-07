'// ==============================================
// RunSpace Voice + Screen Share
// WebRTC Mesh, Max 6 users
// Opus 510kbps stereo + VP9/AV1 4K screen share
// ==============================================

const Voice = (function() {
  const ICE_SERVERS = [
    { urls: "stun:stun.l.google.com:19302" },
    { urls: "stun:stun1.l.google.com:19302" },
    { urls: "stun:stun2.l.google.com:19302" },
    { urls: "stun:stun.cloudflare.com:3478" }
  ];

  const MAX_USERS = 6;
  const QUALITY_KEY = "runspace_voice_quality";

  const AUDIO_PROFILES = {
    ultra: {
      label: "Ultra (510kbps stereo)",
      sampleRate: 48000, channelCount: 2,
      echoCancellation: false, noiseSuppression: false, autoGainControl: false,
      maxBitrate: 510000, stereo: true, dtx: false, fec: true, ptime: 10, maxptime: 10
    },
    high: {
      label: "H\u00f6g (256kbps stereo)",
      sampleRate: 48000, channelCount: 2,
      echoCancellation: false, noiseSuppression: false, autoGainControl: false,
      maxBitrate: 256000, stereo: true, dtx: false, fec: true, ptime: 10, maxptime: 20
    },
    balanced: {
      label: "Balanserad (128kbps)",
      sampleRate: 48000, channelCount: 1,
      echoCancellation: true, noiseSuppression: true, autoGainControl: true,
      maxBitrate: 128000, stereo: false, dtx: false, fec: true, ptime: 20, maxptime: 20
    },
    low: {
      label: "L\u00e5g (64kbps)",
      sampleRate: 48000, channelCount: 1,
      echoCancellation: true, noiseSuppression: true, autoGainControl: true,
      maxBitrate: 64000, stereo: false, dtx: true, fec: true, ptime: 20, maxptime: 40
    }
  };

  const SCREEN_PROFILES = {
    ultra: {
      label: "4K 60fps (max kvalitet)",
      width: 3840, height: 2160, frameRate: 60,
      maxBitrate: 20000000, codec: "VP9", contentHint: "detail"
    },
    high: {
      label: "1440p 60fps",
      width: 2560, height: 1440, frameRate: 60,
      maxBitrate: 12000000, codec: "VP9", contentHint: "detail"
    },
    balanced: {
      label: "1080p 30fps",
      width: 1920, height: 1080, frameRate: 30,
      maxBitrate: 6000000, codec: "VP9", contentHint: "detail"
    },
    motion: {
      label: "1080p 60fps (spel/video)",
      width: 1920, height: 1080, frameRate: 60,
      maxBitrate: 10000000, codec: "VP9", contentHint: "motion"
    }
  };

  const SCREEN_QUALITY_KEY = "runspace_screen_quality";

  let myUsername = "";
  let currentChannel = null;
  let localStream = null;
  let screenStream = null;
  let isMuted = false;
  let isDeafened = false;
  let isScreenSharing = false;
  let signalRConnection = null;
  let inputDeviceId = "default";
  let outputDeviceId = "default";
  let currentAudioQuality = localStorage.getItem(QUALITY_KEY) || "ultra";
  let currentScreenQuality = localStorage.getItem(SCREEN_QUALITY_KEY) || "ultra";

  const peers = new Map();
  const channelUsers = new Map();

  let audioContext = null;
  let analyser = null;
  let speakingInterval = null;
  let isSpeaking = false;
  let onUIUpdate = null;

  function audioProfile() { return AUDIO_PROFILES[currentAudioQuality] || AUDIO_PROFILES.ultra; }
  function screenProfile() { return SCREEN_PROFILES[currentScreenQuality] || SCREEN_PROFILES.ultra; }

  async function startScreenShare() {
    if (isScreenSharing || !currentChannel) return;
    var sp = screenProfile();
    try {
      screenStream = await navigator.mediaDevices.getDisplayMedia({
        video: {
          width: { ideal: sp.width, max: 3840 },
          height: { ideal: sp.height, max: 2160 },
          frameRate: { ideal: sp.frameRate, max: 60 },
          displaySurface: "monitor", cursor: "always"
        },
        audio: {
          echoCancellation: false, noiseSuppression: false, autoGainControl: false,
          sampleRate: 48000, channelCount: 2
        },
        preferCurrentTab: false, selfBrowserSurface: "include",
        surfaceSwitching: "include", systemAudio: "include"
      });
      isScreenSharing = true;
      var videoTrack = screenStream.getVideoTracks()[0];
      if (videoTrack) {
        if (typeof videoTrack.contentHint !== "undefined") videoTrack.contentHint = sp.contentHint || "detail";
        videoTrack.onended = function() { stopScreenShare(); };
      }
      peers.forEach(function(peer, username) { addScreenTracksToPeer(peer); });
      try { signalRConnection.invoke("VoiceScreenShareState", currentChannel, true).catch(function() {}); } catch(e) {}
      if (onUIUpdate) onUIUpdate();
    } catch(e) {
      console.error("Screen share failed:", e);
      isScreenSharing = false; screenStream = null;
      if (e.name !== "NotAllowedError") alert("Kunde inte starta sk\u00e4rmdelning.");
    }
  }

  function stopScreenShare() {
    if (!isScreenSharing) return;
    peers.forEach(function(peer) { removeScreenTracksFromPeer(peer); });
    if (screenStream) { screenStream.getTracks().forEach(function(t) { t.stop(); }); screenStream = null; }
    isScreenSharing = false;
    try { signalRConnection.invoke("VoiceScreenShareState", currentChannel, false).catch(function() {}); } catch(e) {}
    if (onUIUpdate) onUIUpdate();
  }

  function addScreenTracksToPeer(peer) {
    if (!screenStream || !peer.pc) return;
    screenStream.getTracks().forEach(function(track) {
      var senders = peer.pc.getSenders();
      var alreadyAdded = senders.some(function(s) { return s.track && s.track.id === track.id; });
      if (alreadyAdded) return;
      var sender = peer.pc.addTrack(track, screenStream);
      if (track.kind === "video") setTimeout(function() { applyScreenBitrate(sender); }, 200);
    });
    renegotiate(peer);
  }

  function removeScreenTracksFromPeer(peer) {
    if (!peer.pc) return;
    var senders = peer.pc.getSenders();
    senders.forEach(function(sender) {
      if (sender.track && screenStream) {
        var isScreenTrack = screenStream.getTracks().some(function(t) { return t.id === sender.track.id; });
        if (isScreenTrack) { try { peer.pc.removeTrack(sender); } catch(e) {} }
      }
    });
    renegotiate(peer);
  }

  function applyScreenBitrate(sender) {
    if (!sender || !sender.track || sender.track.kind !== "video") return;
    var sp = screenProfile();
    var params = sender.getParameters();
    if (!params.encodings) params.encodings = [{}];
    if (params.encodings.length === 0) params.encodings.push({});
    params.encodings[0].maxBitrate = sp.maxBitrate;
    params.encodings[0].maxFramerate = sp.frameRate;
    params.encodings[0].scaleResolutionDownBy = 1.0;
    params.encodings[0].priority = "high";
    params.encodings[0].networkPriority = "high";
    sender.setParameters(params).catch(function(e) { console.warn("Screen bitrate set failed:", e); });
  }

  async function renegotiate(peer) {
    if (!peer.pc) return;
    try {
      var offer = await peer.pc.createOffer();
      offer.sdp = enhanceSDP(offer.sdp);
      offer.sdp = enhanceVideoSDP(offer.sdp);
      await peer.pc.setLocalDescription(offer);
      var username = null;
      peers.forEach(function(p, u) { if (p === peer) username = u; });
      if (username) await signalRConnection.invoke("SendVoiceOffer", username, peer.pc.localDescription.sdp);
    } catch(e) { console.error("Renegotiation failed:", e); }
  }

  function enhanceVideoSDP(sdp) {
    var sp = screenProfile();
    var vp9Match = sdp.match(/a=rtpmap:(\d+) VP9\/90000/);
    var vp8Match = sdp.match(/a=rtpmap:(\d+) VP8\/90000/);
    var av1Match = sdp.match(/a=rtpmap:(\d+) AV1\/90000/);
    var preferredPt = null;
    if (av1Match) preferredPt = av1Match[1];
    else if (vp9Match) preferredPt = vp9Match[1];
    else if (vp8Match) preferredPt = vp8Match[1];
    if (preferredPt) {
      sdp = sdp.replace(/m=video (\d+) ([A-Z/]+) ([\d ]+)/, function(match, port, proto, payloads) {
        var pts = payloads.split(" ");
        var idx = pts.indexOf(preferredPt);
        if (idx > 0) { pts.splice(idx, 1); pts.unshift(preferredPt); }
        return "m=video " + port + " " + proto + " " + pts.join(" ");
      });
      var fmtpRegex = new RegExp("a=fmtp:" + preferredPt + " [^\\r\\n]+");
      if (fmtpRegex.test(sdp)) {
        sdp = sdp.replace(fmtpRegex, function(match) {
          if (match.indexOf("x-google-max-bitrate") === -1) return match + ";x-google-max-bitrate=" + (sp.maxBitrate / 1000);
          return match;
        });
      }
    }
    sdp = sdp.replace(/b=AS:\d+\r\n/g, "");
    return sdp;
  }

  function setAudioQuality(q) {
    if (!AUDIO_PROFILES[q]) return;
    currentAudioQuality = q; localStorage.setItem(QUALITY_KEY, q);
    if (currentChannel) { peers.forEach(function(peer) { applyAudioBitrate(peer.pc); }); restartMicrophone(); }
    if (onUIUpdate) onUIUpdate();
  }

  function setScreenQuality(q) {
    if (!SCREEN_PROFILES[q]) return;
    currentScreenQuality = q; localStorage.setItem(SCREEN_QUALITY_KEY, q);
    if (isScreenSharing) {
      peers.forEach(function(peer) {
        var senders = peer.pc.getSenders();
        senders.forEach(function(s) { if (s.track && s.track.kind === "video") applyScreenBitrate(s); });
      });
    }
    if (onUIUpdate) onUIUpdate();
  }

  function getAudioQualityOptions() {
    return Object.entries(AUDIO_PROFILES).map(function(e) { return { id: e[0], label: e[1].label, active: e[0] === currentAudioQuality }; });
  }

  function getScreenQualityOptions() {
    return Object.entries(SCREEN_PROFILES).map(function(e) { return { id: e[0], label: e[1].label, active: e[0] === currentScreenQuality }; });
  }

  function applyAudioBitrate(pc) {
    var ap = audioProfile();
    pc.getSenders().forEach(function(sender) {
      if (!sender.track || sender.track.kind !== "audio") return;
      if (screenStream && screenStream.getAudioTracks().some(function(t) { return t.id === sender.track.id; })) return;
      var params = sender.getParameters();
      if (!params.encodings) params.encodings = [{}];
      if (params.encodings.length === 0) params.encodings.push({});
      params.encodings[0].maxBitrate = ap.maxBitrate;
      if (ap.ptime) params.encodings[0].ptime = ap.ptime;
      sender.setParameters(params).catch(function() {});
    });
  }

  function enhanceSDP(sdp) {
    var ap = audioProfile();
    var opusMatch = sdp.match(/a=rtpmap:(\d+) opus\/48000\/2/);
    if (!opusMatch) return sdp;
    var pt = opusMatch[1];
    var fmtpRegex = new RegExp("a=fmtp:" + pt + " [^\\r\\n]+", "g");
    sdp = sdp.replace(fmtpRegex, "");
    var fmtp = "a=fmtp:" + pt +
      " minptime=" + ap.ptime + ";useinbandfec=" + (ap.fec ? "1" : "0") +
      ";usedtx=" + (ap.dtx ? "1" : "0") + ";stereo=" + (ap.stereo ? "1" : "0") +
      ";sprop-stereo=" + (ap.stereo ? "1" : "0") + ";maxaveragebitrate=" + ap.maxBitrate +
      ";maxplaybackrate=" + ap.sampleRate + ";cbr=1";
    if (ap.maxptime) fmtp += ";maxptime=" + ap.maxptime;
    sdp = sdp.replace(opusMatch[0], opusMatch[0] + "\r\n" + fmtp);
    sdp = sdp.replace(/m=audio (\d+) ([A-Z/]+) ([\d ]+)/, function(match, port, proto, payloads) {
      var pts = payloads.split(" ");
      var idx = pts.indexOf(pt);
      if (idx > 0) { pts.splice(idx, 1); pts.unshift(pt); }
      return "m=audio " + port + " " + proto + " " + pts.join(" ");
    });
    return sdp;
  }

  async function getMicStream() {
    var ap = audioProfile();
    return navigator.mediaDevices.getUserMedia({
      audio: {
        deviceId: inputDeviceId !== "default" ? { exact: inputDeviceId } : undefined,
        sampleRate: { ideal: ap.sampleRate }, channelCount: { ideal: ap.channelCount },
        echoCancellation: { ideal: ap.echoCancellation }, noiseSuppression: { ideal: ap.noiseSuppression },
        autoGainControl: { ideal: ap.autoGainControl }, latency: { ideal: 0.003 }, sampleSize: { ideal: 24 }
      }, video: false
    });
  }

  async function restartMicrophone() {
    if (!currentChannel) return;
    if (localStream) localStream.getTracks().forEach(function(t) { t.stop(); });
    try {
      localStream = await getMicStream();
      if (isMuted) localStream.getAudioTracks().forEach(function(t) { t.enabled = false; });
      var track = localStream.getAudioTracks()[0];
      if (track) {
        peers.forEach(function(peer) {
          var sender = peer.pc.getSenders().find(function(s) {
            return s.track && s.track.kind === "audio" &&
              (!screenStream || !screenStream.getAudioTracks().some(function(st) { return st.id === s.track.id; }));
          });
          if (sender) sender.replaceTrack(track).catch(function() {});
        });
      }
      if (speakingInterval) clearInterval(speakingInterval);
      if (audioContext) { try { audioContext.close(); } catch(e) {} }
      setupSpeakingDetection();
    } catch(e) { console.error("Mic restart failed:", e); }
  }

  async function getAudioDevices() {
    try {
      var devices = await navigator.mediaDevices.enumerateDevices();
      return { inputs: devices.filter(function(d) { return d.kind === "audioinput"; }), outputs: devices.filter(function(d) { return d.kind === "audiooutput"; }) };
    } catch(e) { return { inputs: [], outputs: [] }; }
  }

  function setInputDevice(id) { inputDeviceId = id; if (currentChannel) restartMicrophone(); }
  function setOutputDevice(id) {
    outputDeviceId = id;
    peers.forEach(function(p) { if (p.audio && typeof p.audio.setSinkId === "function") p.audio.setSinkId(id).catch(function() {}); });
  }

  function setupSpeakingDetection() {
    if (!localStream) return;
    try {
      audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
      var source = audioContext.createMediaStreamSource(localStream);
      analyser = audioContext.createAnalyser();
      analyser.fftSize = 512; analyser.smoothingTimeConstant = 0.35;
      source.connect(analyser);
      var buf = new Uint8Array(analyser.frequencyBinCount);
      speakingInterval = setInterval(function() {
        if (!analyser || isMuted) { if (isSpeaking) { isSpeaking = false; broadcastSpeaking(false); } return; }
        analyser.getByteFrequencyData(buf);
        var sum = 0; for (var i = 0; i < buf.length; i++) sum += buf[i];
        var was = isSpeaking;
        isSpeaking = (sum / buf.length) > 12;
        if (was !== isSpeaking) { broadcastSpeaking(isSpeaking); if (onUIUpdate) onUIUpdate(); }
      }, 80);
    } catch(e) { console.error("Speaking detect failed:", e); }
  }

  function broadcastSpeaking(s) {
    try { signalRConnection.invoke("VoiceSpeakingState", currentChannel, s).catch(function() {}); } catch(e) {}
  }

  function handleChannelState(channels) {
    channelUsers.clear();
    if (Array.isArray(channels)) channels.forEach(function(ch) { channelUsers.set(ch.name, Array.isArray(ch.users) ? ch.users : []); });
    if (onUIUpdate) onUIUpdate();
  }

  function handleUserJoined(channel, username) {
    if (!channelUsers.has(channel)) channelUsers.set(channel, []);
    var users = channelUsers.get(channel);
    if (!users.find(function(u) { return u.username === username; })) users.push({ username: username, speaking: false, sharing: false });
    if (channel === currentChannel && username !== myUsername) createPeerConnection(username, true);
    if (onUIUpdate) onUIUpdate();
  }

  function handleUserLeft(channel, username) {
    if (channelUsers.has(channel)) {
      var users = channelUsers.get(channel);
      var idx = users.findIndex(function(u) { return u.username === username; });
      if (idx >= 0) users.splice(idx, 1);
    }
    destroyPeer(username);
    if (onUIUpdate) onUIUpdate();
  }

  async function handleOffer(fromUser, offerSdp) {
    if (!currentChannel || !localStream) return;
    var peer = createPeerConnection(fromUser, false);
    try {
      await peer.pc.setRemoteDescription(new RTCSessionDescription({ type: "offer", sdp: offerSdp }));
      var answer = await peer.pc.createAnswer();
      answer.sdp = enhanceSDP(answer.sdp); answer.sdp = enhanceVideoSDP(answer.sdp);
      await peer.pc.setLocalDescription(answer);
      await signalRConnection.invoke("SendVoiceAnswer", fromUser, answer.sdp);
    } catch(e) { console.error("Handle offer failed:", e); }
  }

  async function handleAnswer(fromUser, answerSdp) {
    var peer = peers.get(fromUser); if (!peer) return;
    try {
      await peer.pc.setRemoteDescription(new RTCSessionDescription({ type: "answer", sdp: answerSdp }));
      setTimeout(function() {
        applyAudioBitrate(peer.pc);
        if (isScreenSharing) peer.pc.getSenders().forEach(function(s) { if (s.track && s.track.kind === "video") applyScreenBitrate(s); });
      }, 500);
    } catch(e) { console.error("Handle answer failed:", e); }
  }

  async function handleIceCandidate(fromUser, json) {
    var peer = peers.get(fromUser); if (!peer) return;
    try { await peer.pc.addIceCandidate(new RTCIceCandidate(JSON.parse(json))); } catch(e) {}
  }

  function handleSpeaking(username, speaking) {
    channelUsers.forEach(function(users) { var u = users.find(function(x) { return x.username === username; }); if (u) u.speaking = speaking; });
    if (peers.has(username)) peers.get(username).speaking = speaking;
    if (onUIUpdate) onUIUpdate();
  }

  function handleScreenShareState(username, sharing) {
    channelUsers.forEach(function(users) { var u = users.find(function(x) { return x.username === username; }); if (u) u.sharing = sharing; });
    if (onUIUpdate) onUIUpdate();
  }

  function createPeerConnection(remoteUser, isInitiator) {
    if (peers.has(remoteUser)) return peers.get(remoteUser);
    var pc = new RTCPeerConnection({ iceServers: ICE_SERVERS, sdpSemantics: "unified-plan" });
    var audio = new Audio(); audio.autoplay = true; audio.muted = isDeafened;
    if (outputDeviceId !== "default" && typeof audio.setSinkId === "function") audio.setSinkId(outputDeviceId).catch(function() {});
    var videoEl = document.createElement("video"); videoEl.autoplay = true; videoEl.playsInline = true; videoEl.muted = true; videoEl.style.display = "none";
    var peerData = { pc: pc, audio: audio, videoEl: videoEl, speaking: false };
    peers.set(remoteUser, peerData);
    if (localStream) localStream.getTracks().forEach(function(track) { pc.addTrack(track, localStream); });
    if (isScreenSharing && screenStream) screenStream.getTracks().forEach(function(track) { pc.addTrack(track, screenStream); });
    pc.ontrack = function(event) {
      if (event.track.kind === "audio") { if (event.streams[0]) audio.srcObject = event.streams[0]; }
      else if (event.track.kind === "video") {
        if (event.streams[0]) { videoEl.srcObject = event.streams[0]; videoEl.style.display = "block"; showScreenShareViewer(remoteUser, videoEl); }
        event.track.onended = function() { videoEl.style.display = "none"; hideScreenShareViewer(remoteUser); };
      }
    };
    pc.onicecandidate = function(event) {
      if (event.candidate) { try { signalRConnection.invoke("SendVoiceIceCandidate", remoteUser, JSON.stringify(event.candidate)).catch(function() {}); } catch(e) {} }
    };
    pc.onconnectionstatechange = function() {
      if (pc.connectionState === "connected") { applyAudioBitrate(pc); if (isScreenSharing) pc.getSenders().forEach(function(s) { if (s.track && s.track.kind === "video") applyScreenBitrate(s); }); }
      if (pc.connectionState === "failed") pc.restartIce();
    };
    pc.onnegotiationneeded = function() {
      if (pc._negotiating) return; pc._negotiating = true;
      setTimeout(function() { pc._negotiating = false; if (isInitiator || pc.signalingState === "stable") renegotiate(peerData); }, 200);
    };
    if (isInitiator) {
      pc.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: true, voiceActivityDetection: false })
        .then(function(offer) { offer.sdp = enhanceSDP(offer.sdp); offer.sdp = enhanceVideoSDP(offer.sdp); return pc.setLocalDescription(offer); })
        .then(function() { return signalRConnection.invoke("SendVoiceOffer", remoteUser, pc.localDescription.sdp); })
        .catch(function(e) { console.error("Offer failed:", e); });
    }
    return peerData;
  }

  function destroyPeer(username) {
    if (!peers.has(username)) return;
    var peer = peers.get(username);
    if (peer.pc) peer.pc.close();
    if (peer.audio) { peer.audio.pause(); peer.audio.srcObject = null; }
    if (peer.videoEl) { peer.videoEl.pause(); peer.videoEl.srcObject = null; }
    hideScreenShareViewer(username); peers.delete(username);
  }

  function showScreenShareViewer(username, videoEl) {
    var existing = document.getElementById("screen-viewer-" + username); if (existing) existing.remove();
    var container = document.getElementById("screenViewerArea"); if (!container) return;
    var wrapper = document.createElement("div"); wrapper.id = "screen-viewer-" + username; wrapper.className = "screen-viewer";
    wrapper.innerHTML = '<div class="screen-viewer-header"><span class="screen-viewer-user">\ud83d\udda5\ufe0f ' + username + ' delar sk\u00e4rm</span><button class="screen-viewer-fullscreen" title="Fullsk\u00e4rm">\u26f6</button></div>';
    videoEl.className = "screen-viewer-video"; videoEl.style.display = "block";
    wrapper.appendChild(videoEl); container.appendChild(wrapper); container.classList.remove("hidden");
    wrapper.querySelector(".screen-viewer-fullscreen").addEventListener("click", function() { if (videoEl.requestFullscreen) videoEl.requestFullscreen(); else if (videoEl.webkitRequestFullscreen) videoEl.webkitRequestFullscreen(); });
    videoEl.addEventListener("dblclick", function() { if (videoEl.requestFullscreen) videoEl.requestFullscreen(); else if (videoEl.webkitRequestFullscreen) videoEl.webkitRequestFullscreen(); });
  }

  function hideScreenShareViewer(username) {
    var el = document.getElementById("screen-viewer-" + username); if (el) el.remove();
    var container = document.getElementById("screenViewerArea"); if (container && container.children.length === 0) container.classList.add("hidden");
  }

  function init(username, connection, uiCallback) {
    myUsername = username; signalRConnection = connection; onUIUpdate = uiCallback;
    connection.on("VoiceChannelState", handleChannelState);
    connection.on("VoiceUserJoined", handleUserJoined);
    connection.on("VoiceUserLeft", handleUserLeft);
    connection.on("VoiceOffer", handleOffer);
    connection.on("VoiceAnswer", handleAnswer);
    connection.on("VoiceIceCandidate", handleIceCandidate);
    connection.on("VoiceSpeaking", handleSpeaking);
    connection.on("VoiceScreenShareState", handleScreenShareState);
    try { connection.invoke("GetVoiceChannels").catch(function() {}); } catch(e) {}
  }

  async function joinChannel(channelName) {
    if (currentChannel === channelName) return;
    if (currentChannel) await leaveChannel();
    try {
      localStream = await getMicStream();
      currentChannel = channelName; setupSpeakingDetection();
      await signalRConnection.invoke("JoinVoiceChannel", channelName);
      if (onUIUpdate) onUIUpdate();
    } catch(e) {
      console.error("Join failed:", e); cleanup();
      if (e.name === "NotAllowedError") alert("Mikrofontillg\u00e5ng nekad.");
      else if (e.name === "NotFoundError") alert("Ingen mikrofon hittad.");
      else alert("Kunde inte g\u00e5 med.");
    }
  }

  async function leaveChannel() {
    if (!currentChannel) return;
    if (isScreenSharing) stopScreenShare();
    try { await signalRConnection.invoke("LeaveVoiceChannel", currentChannel); } catch(e) {}
    cleanup(); if (onUIUpdate) onUIUpdate();
  }

  function toggleMute() {
    isMuted = !isMuted;
    if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = !isMuted; });
    if (onUIUpdate) onUIUpdate();
  }

  function toggleDeafen() {
    isDeafened = !isDeafened;
    peers.forEach(function(p) { if (p.audio) p.audio.muted = isDeafened; });
    if (isDeafened && !isMuted) { isMuted = true; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = false; }); }
    else if (!isDeafened && isMuted) { isMuted = false; if (localStream) localStream.getAudioTracks().forEach(function(t) { t.enabled = true; }); }
    if (onUIUpdate) onUIUpdate();
  }

  function getState() {
    return {
      currentChannel: currentChannel, isMuted: isMuted, isDeafened: isDeafened,
      isSpeaking: isSpeaking, isScreenSharing: isScreenSharing,
      channelUsers: Object.fromEntries(channelUsers),
      peers: Array.from(peers.keys()), myUsername: myUsername,
      audioQuality: currentAudioQuality, audioQualityLabel: audioProfile().label,
      screenQuality: currentScreenQuality, screenQualityLabel: screenProfile().label
    };
  }

  function getDefaultChannels() {
    return [
      { name: "H\u00e4ng", icon: "\ud83d\udd0a" }, { name: "Gaming", icon: "\ud83c\udfae" },
      { name: "Musik", icon: "\ud83c\udfb5" }, { name: "AFK", icon: "\ud83d\udca4" }
    ];
  }

  function cleanup() {
    if (speakingInterval) { clearInterval(speakingInterval); speakingInterval = null; }
    if (audioContext) { try { audioContext.close(); } catch(e) {} audioContext = null; analyser = null; }
    peers.forEach(function(p, u) { destroyPeer(u); }); peers.clear();
    if (localStream) { localStream.getTracks().forEach(function(t) { t.stop(); }); localStream = null; }
    if (screenStream) { screenStream.getTracks().forEach(function(t) { t.stop(); }); screenStream = null; }
    isSpeaking = false; isMuted = false; isDeafened = false; isScreenSharing = false; currentChannel = null;
    var area = document.getElementById("screenViewerArea"); if (area) { area.innerHTML = ""; area.classList.add("hidden"); }
  }

  function renderChannelsHTML() {
    var state = getState(); var channels = getDefaultChannels(); var html = "";
    channels.forEach(function(ch) {
      var users = channelUsers.get(ch.name) || [];
      var isActive = state.currentChannel === ch.name;
      var isFull = users.length >= MAX_USERS;
      html += '<div class="vc-channel' + (isActive ? " active" : "") + (isFull ? " full" : "") + '" data-channel="' + ch.name + '">';
      html += '<div class="vc-channel-head"><span class="vc-channel-icon">' + ch.icon + '</span><span class="vc-channel-name">' + ch.name + '</span><span class="vc-channel-count">' + users.length + '/' + MAX_USERS + '</span></div>';
      if (users.length > 0) {
        html += '<div class="vc-users">';
        users.forEach(function(u) {
          var isMe = u.username === myUsername; var muted = isMe && state.isMuted;
          html += '<div class="vc-user' + (u.speaking ? " speaking" : "") + (muted ? " muted" : "") + '">';
          html += '<div class="vc-user-avatar' + (u.speaking ? " speaking" : "") + '"></div>';
          html += '<span class="vc-user-name">' + u.username + '</span>';
          if (u.sharing) html += '<span class="vc-user-badge">\ud83d\udda5\ufe0f</span>';
          if (muted) html += '<span class="vc-user-muted">\ud83d\udd07</span>';
          html += '</div>';
        });
        html += '</div>';
      }
      html += '</div>';
    });
    if (state.currentChannel) {
      html += '<div class="vc-controls">';
      html += '<button class="vc-ctrl-btn' + (state.isMuted ? " active" : "") + '" id="vcMuteBtn">' + (state.isMuted ? "\ud83d\udd07" : "\ud83c\udf99\ufe0f") + '</button>';
      html += '<button class="vc-ctrl-btn' + (state.isDeafened ? " active" : "") + '" id="vcDeafenBtn">' + (state.isDeafened ? "\ud83d\udd15" : "\ud83d\udd0a") + '</button>';
      html += '<button class="vc-ctrl-btn' + (state.isScreenSharing ? " active screen" : "") + '" id="vcScreenBtn" title="Sk\u00e4rmdelning">' + (state.isScreenSharing ? "\ud83d\udfe2" : "\ud83d\udda5\ufe0f") + '</button>';
      html += '<button class="vc-ctrl-btn disconnect" id="vcLeaveBtn">\ud83d\udcde</button>';
      html += '</div>';
      html += '<div class="vc-settings">';
      html += '<details class="vc-details"><summary>Ljudkvalitet: ' + state.audioQualityLabel + '</summary><div class="vc-quality-options">';
      getAudioQualityOptions().forEach(function(o) { html += '<button class="vc-quality-btn' + (o.active ? " active" : "") + '" data-aquality="' + o.id + '">' + o.label + '</button>'; });
      html += '</div></details>';
      html += '<details class="vc-details"><summary>Sk\u00e4rmkvalitet: ' + state.screenQualityLabel + '</summary><div class="vc-quality-options">';
      getScreenQualityOptions().forEach(function(o) { html += '<button class="vc-quality-btn' + (o.active ? " active" : "") + '" data-squality="' + o.id + '">' + o.label + '</button>'; });
      html += '</div></details></div>';
    }
    return html;
  }

  function bindChannelEvents(container) {
    container.querySelectorAll(".vc-channel").forEach(function(el) {
      el.addEventListener("click", function() { var ch = el.dataset.channel; if (ch && ch !== currentChannel) joinChannel(ch); });
    });
    var m = container.querySelector("#vcMuteBtn"); var d = container.querySelector("#vcDeafenBtn");
    var s = container.querySelector("#vcScreenBtn"); var l = container.querySelector("#vcLeaveBtn");
    if (m) m.addEventListener("click", function(e) { e.stopPropagation(); toggleMute(); });
    if (d) d.addEventListener("click", function(e) { e.stopPropagation(); toggleDeafen(); });
    if (s) s.addEventListener("click", function(e) { e.stopPropagation(); isScreenSharing ? stopScreenShare() : startScreenShare(); });
    if (l) l.addEventListener("click", function(e) { e.stopPropagation(); leaveChannel(); });
    container.querySelectorAll("[data-aquality]").forEach(function(btn) { btn.addEventListener("click", function(e) { e.stopPropagation(); setAudioQuality(btn.dataset.aquality); }); });
    container.querySelectorAll("[data-squality]").forEach(function(btn) { btn.addEventListener("click", function(e) { e.stopPropagation(); setScreenQuality(btn.dataset.squality); }); });
  }

  return {
    init: init, joinChannel: joinChannel, leaveChannel: leaveChannel,
    toggleMute: toggleMute, toggleDeafen: toggleDeafen,
    startScreenShare: startScreenShare, stopScreenShare: stopScreenShare,
    getState: getState, getDefaultChannels: getDefaultChannels,
    renderChannelsHTML: renderChannelsHTML, bindChannelEvents: bindChannelEvents,
    cleanup: cleanup,
    setAudioQuality: setAudioQuality, setScreenQuality: setScreenQuality,
    getAudioQualityOptions: getAudioQualityOptions, getScreenQualityOptions: getScreenQualityOptions,
    getAudioDevices: getAudioDevices, setInputDevice: setInputDevice, setOutputDevice: setOutputDevice
  };
})();
