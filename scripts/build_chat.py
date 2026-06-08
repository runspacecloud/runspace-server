html = r"""<!DOCTYPE html>
<html lang="sv">
<head>
  <meta charset="UTF-8"/>
  <meta name="viewport" content="width=device-width,initial-scale=1.0"/>
  <title>RunSpace</title>
  <link rel="preconnect" href="https://fonts.googleapis.com"/>
  <link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=Syne:wght@400;500;600;700&display=swap" rel="stylesheet"/>
  <style>
    :root {
      --bg:#07080d;--s1:#0c0d14;--s2:#10111a;--s3:#161722;--s4:#1d1e2c;--s5:#232435;
      --b1:rgba(255,255,255,.05);--b2:rgba(255,255,255,.09);--b3:rgba(255,255,255,.14);
      --tx:#d8daf0;--mu:#3e4060;--mu2:#686a8a;--mu3:#9496b0;
      --ac:#4f6ef7;--ac2:#3b55e0;--ac-glow:rgba(79,110,247,.18);
      --gn:#1fd882;--gn-dim:rgba(31,216,130,.1);--gn-b:rgba(31,216,130,.22);
      --ye:#f0b429;--ye-dim:rgba(240,180,41,.08);
      --rd:#e04444;--rd-dim:rgba(224,68,68,.08);
      --mono:'IBM Plex Mono',monospace;--sans:'Syne',sans-serif;
      --rail:68px;--sidebar:240px;
    }
    *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
    html,body{height:100%;background:var(--bg);color:var(--tx);font-family:var(--sans);overflow:hidden}

    /* ── SHELL ── */
    .shell{display:grid;grid-template-columns:var(--rail) var(--sidebar) 1fr;grid-template-rows:1fr;height:100vh}

    /* ── RAIL (server icons) ── */
    .rail{background:#09090f;border-right:1px solid var(--b1);display:flex;flex-direction:column;align-items:center;padding:10px 0;gap:6px;overflow-y:auto;overflow-x:hidden}
    .rail::-webkit-scrollbar{display:none}
    .rail-sep{width:32px;height:1px;background:var(--b1);margin:4px 0;flex-shrink:0}
    .rail-item{position:relative;width:46px;height:46px;border-radius:16px;background:var(--s2);border:1px solid var(--b1);display:flex;align-items:center;justify-content:center;cursor:pointer;font-size:18px;flex-shrink:0;transition:border-radius .2s,background .15s,border-color .15s;color:var(--mu3)}
    .rail-item:hover{border-radius:12px;background:var(--s3);border-color:var(--b2)}
    .rail-item.active{border-radius:12px;background:rgba(79,110,247,.18);border-color:rgba(79,110,247,.4);color:var(--ac)}
    .rail-item.dm-btn{font-size:16px}
    .rail-item.add-btn{font-size:20px;color:var(--gn);border-color:var(--gn-b);background:var(--gn-dim)}
    .rail-item.add-btn:hover{background:rgba(31,216,130,.2)}
    .rail-badge{position:absolute;top:-3px;right:-3px;min-width:16px;height:16px;border-radius:999px;background:var(--rd);color:white;font-family:var(--mono);font-size:9px;display:flex;align-items:center;justify-content:center;padding:0 3px;border:2px solid #09090f}
    .rail-tooltip{position:absolute;left:calc(var(--rail) - 4px);background:#1a1b2e;border:1px solid var(--b2);border-radius:6px;padding:5px 10px;font-size:11px;white-space:nowrap;color:var(--tx);pointer-events:none;opacity:0;transition:opacity .15s;z-index:100}
    .rail-item:hover .rail-tooltip{opacity:1}

    /* ── SIDEBAR ── */
    .sidebar{background:var(--s1);border-right:1px solid var(--b1);display:flex;flex-direction:column;overflow:hidden}
    .sidebar-header{padding:14px 14px 10px;border-bottom:1px solid var(--b1);flex-shrink:0}
    .sidebar-title{font-size:13px;font-weight:700;color:var(--tx);display:flex;align-items:center;justify-content:space-between;gap:8px}
    .sidebar-title .icon-btn{width:24px;height:24px;font-size:14px}
    .sidebar-sub{font-size:10px;color:var(--mu2);margin-top:3px;font-family:var(--mono)}
    .section-label{font-family:var(--mono);font-size:9px;text-transform:uppercase;letter-spacing:.7px;color:var(--mu);padding:10px 14px 5px;flex-shrink:0;display:flex;align-items:center;justify-content:space-between}
    .section-label button{background:none;border:none;color:var(--mu2);cursor:pointer;font-size:14px;line-height:1;padding:0 2px;transition:color .15s}
    .section-label button:hover{color:var(--tx)}
    .sidebar-scroll{flex:1;overflow-y:auto;overflow-x:hidden;min-height:0}
    .sidebar-scroll::-webkit-scrollbar{width:3px}
    .sidebar-scroll::-webkit-scrollbar-thumb{background:var(--s4);border-radius:2px}

    /* me block */
    .me-block{display:flex;align-items:center;gap:10px;padding:10px 14px;border-top:1px solid var(--b1);flex-shrink:0;background:var(--s1)}
    .av{width:32px;height:32px;border-radius:9px;background:var(--ac);display:flex;align-items:center;justify-content:center;font-family:var(--mono);font-size:13px;font-weight:500;color:white;flex-shrink:0}
    .me-info{min-width:0;flex:1}
    .me-name{font-size:12px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .me-status{font-size:10px;color:var(--mu3);font-family:var(--mono)}
    .vbadge{display:inline-flex;align-items:center;gap:3px;padding:1px 5px;border-radius:4px;background:var(--gn-dim);border:1px solid var(--gn-b);color:var(--gn);font-family:var(--mono);font-size:8px}

    /* conv items */
    .conv-item{display:flex;align-items:center;gap:10px;padding:7px 14px;cursor:pointer;border-left:2px solid transparent;transition:background .1s,border-color .1s;min-width:0}
    .conv-item:hover{background:var(--s2)}
    .conv-item.active{background:rgba(79,110,247,.1);border-left-color:var(--ac)}
    .conv-av{width:32px;height:32px;border-radius:9px;background:var(--s4);border:1px solid var(--b1);display:flex;align-items:center;justify-content:center;font-family:var(--mono);font-size:12px;color:var(--mu3);flex-shrink:0}
    .conv-av.server{border-radius:12px}
    .conv-info{min-width:0;flex:1}
    .conv-name{font-size:12px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .conv-preview{font-size:10px;color:var(--mu3);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-family:var(--mono)}
    .conv-meta{display:flex;flex-direction:column;align-items:flex-end;gap:3px;flex-shrink:0}
    .conv-time{font-family:var(--mono);font-size:9px;color:var(--mu2)}
    .unread-badge{min-width:16px;height:16px;border-radius:999px;padding:0 4px;background:var(--ac);color:white;font-family:var(--mono);font-size:9px;display:flex;align-items:center;justify-content:center}

    /* channel items */
    .ch-item{display:flex;align-items:center;gap:8px;padding:5px 14px 5px 20px;cursor:pointer;border-radius:0;transition:background .1s;color:var(--mu3);font-size:12px;min-width:0}
    .ch-item:hover{background:var(--s2);color:var(--tx)}
    .ch-item.active{background:rgba(79,110,247,.1);color:var(--tx)}
    .ch-icon{font-size:13px;flex-shrink:0;width:16px;text-align:center}
    .ch-name{flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:500}

    /* search */
    .rs-search{width:100%;background:var(--s2);border:1px solid var(--b1);border-radius:8px;padding:7px 10px;font:12px/1 var(--sans);color:var(--tx);outline:none;transition:border-color .15s}
    .rs-search:focus{border-color:rgba(79,110,247,.4)}
    .rs-search::placeholder{color:var(--mu2)}

    /* ── MAIN ── */
    .main{display:flex;flex-direction:column;overflow:hidden;background:var(--bg);position:relative}
    .main::before{content:'';position:absolute;inset:0;background-image:linear-gradient(var(--b1) 1px,transparent 1px),linear-gradient(90deg,var(--b1) 1px,transparent 1px);background-size:32px 32px;pointer-events:none;z-index:0;opacity:.5}
    .main>*{position:relative;z-index:1}

    /* chat header */
    .chat-header{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:0 20px;height:54px;border-bottom:1px solid var(--b1);background:rgba(7,8,13,.85);backdrop-filter:blur(12px);flex-shrink:0}
    .chat-header-left{display:flex;align-items:center;gap:10px;min-width:0;flex:1}
    .chat-peer-av{width:28px;height:28px;border-radius:8px;background:var(--s4);border:1px solid var(--b2);display:flex;align-items:center;justify-content:center;font-family:var(--mono);font-size:11px;color:var(--mu3);flex-shrink:0}
    .chat-peer-name{font-size:14px;font-weight:700}
    .chat-peer-sub{font-size:10px;color:var(--mu3);font-family:var(--mono)}
    .privacy-tag{display:inline-flex;align-items:center;gap:4px;padding:2px 7px;border-radius:4px;font-family:var(--mono);font-size:9px;font-weight:500;flex-shrink:0}
    .privacy-tag.dm{background:rgba(79,110,247,.12);border:1px solid rgba(79,110,247,.25);color:#93b4fd}
    .privacy-tag.group{background:rgba(240,180,41,.08);border:1px solid rgba(240,180,41,.2);color:var(--ye)}
    .privacy-tag.server{background:rgba(31,216,130,.08);border:1px solid var(--gn-b);color:var(--gn)}
    .chat-actions{display:flex;gap:6px;flex-shrink:0}

    /* conn pill */
    .conn-pill{display:flex;align-items:center;gap:5px;padding:4px 9px;border-radius:999px;background:var(--s2);border:1px solid var(--b1);font-family:var(--mono);font-size:10px;color:var(--mu2);transition:all .3s}
    .conn-pill.connected{border-color:var(--gn-b);background:var(--gn-dim);color:var(--gn)}
    .conn-pill.error{border-color:rgba(224,68,68,.25);background:var(--rd-dim);color:var(--rd)}
    .conn-pill.warning{border-color:rgba(240,180,41,.22);background:var(--ye-dim);color:var(--ye)}
    .conn-dot{width:5px;height:5px;border-radius:50%;background:currentColor;animation:blink 2s ease-in-out infinite}
    .conn-pill.connected .conn-dot{animation:none}
    @keyframes blink{0%,100%{opacity:1}50%{opacity:.3}}

    /* icon btn */
    .icon-btn{width:30px;height:30px;border:1px solid var(--b1);background:var(--s2);border-radius:8px;display:flex;align-items:center;justify-content:center;cursor:pointer;color:var(--mu3);transition:color .15s,border-color .15s,background .15s;flex-shrink:0}
    .icon-btn:hover{color:var(--tx);border-color:var(--b2);background:var(--s3)}
    .icon-btn svg{width:14px;height:14px}

    /* messages */
    .messages{flex:1;min-height:0;overflow-y:auto;padding:16px 20px 8px;display:flex;flex-direction:column;gap:2px;scroll-behavior:smooth}
    .messages::-webkit-scrollbar{width:4px}
    .messages::-webkit-scrollbar-thumb{background:var(--s4);border-radius:2px}

    /* empty / welcome */
    .welcome-screen{margin:auto;text-align:center;max-width:340px;padding:20px}
    .welcome-icon{width:56px;height:56px;border-radius:16px;background:var(--s2);border:1px solid var(--b1);display:flex;align-items:center;justify-content:center;margin:0 auto 16px;font-size:24px}
    .welcome-title{font-size:17px;font-weight:700;margin-bottom:8px}
    .welcome-sub{font-size:12px;color:var(--mu3);line-height:1.6;margin-bottom:20px}
    .welcome-actions{display:flex;gap:8px;justify-content:center;flex-wrap:wrap}
    .w-btn{padding:9px 16px;border-radius:10px;font:600 12px/1 var(--sans);border:none;cursor:pointer;transition:filter .15s,transform .08s;display:flex;align-items:center;gap:6px}
    .w-btn:hover{filter:brightness(1.08)}
    .w-btn:active{transform:translateY(1px)}
    .w-btn.primary{background:var(--ac);color:white}
    .w-btn.ghost{background:var(--s3);border:1px solid var(--b2);color:var(--mu3)}
    .w-btn.ghost:hover{color:var(--tx)}
    .w-btn.green{background:var(--gn-dim);border:1px solid var(--gn-b);color:var(--gn)}

    /* message rows */
    .msg-row{display:flex;flex-direction:column;gap:2px;padding:1px 0;animation:msg-in .15s ease}
    @keyframes msg-in{from{opacity:0;transform:translateY(4px)}to{opacity:1;transform:translateY(0)}}
    .msg-row.mine{align-items:flex-end}
    .msg-row.other{align-items:flex-start}
    .msg-row.system{align-items:center}
    .msg-row.other+.msg-row.mine,.msg-row.mine+.msg-row.other{margin-top:10px}
    .msg-meta{display:flex;align-items:center;gap:6px;padding:0 2px;font-family:var(--mono);font-size:10px;color:var(--mu2)}
    .msg-bubble{max-width:min(72%,660px);padding:9px 13px;border-radius:14px;font-size:13px;line-height:1.5;white-space:pre-wrap;word-break:break-word}
    .msg-row.mine .msg-bubble{background:var(--ac);color:white;border-bottom-right-radius:4px}
    .msg-row.other .msg-bubble{background:var(--s3);border:1px solid var(--b1);color:var(--tx);border-bottom-left-radius:4px}
    .msg-row.system .msg-bubble{background:transparent;border:1px solid var(--b1);color:var(--mu3);font-size:11px;font-family:var(--mono);padding:4px 12px;border-radius:6px;max-width:none}
    .msg-status{font-family:var(--mono);font-size:9px;padding:1px 5px;border-radius:4px;border:1px solid var(--b1);background:rgba(255,255,255,.04)}
    .msg-status.pending{color:#93b4fd}
    .msg-status.sent{color:var(--gn)}
    .msg-status.failed{color:var(--rd);cursor:pointer}
    .date-divider{display:flex;align-items:center;gap:10px;margin:12px 0 6px;color:var(--mu2);font-family:var(--mono);font-size:10px}
    .date-divider::before,.date-divider::after{content:'';flex:1;height:1px;background:var(--b2)}

    /* typing */
    .typing-row{padding:4px 0;min-height:24px}
    .typing-indicator{display:inline-flex;background:var(--s3);border:1px solid var(--b1);border-radius:12px;padding:8px 12px;gap:4px;align-items:center}
    .typing-dot{width:4px;height:4px;border-radius:50%;background:var(--mu3);animation:tb .9s ease-in-out infinite}
    .typing-dot:nth-child(2){animation-delay:.15s}
    .typing-dot:nth-child(3){animation-delay:.3s}
    @keyframes tb{0%,100%{transform:translateY(0);opacity:.5}50%{transform:translateY(-3px);opacity:1}}
    .typing-label{font-family:var(--mono);font-size:10px;color:var(--mu2);margin-left:6px}

    /* banner */
    .banner{margin:8px 16px 0;padding:8px 14px;border-radius:8px;font-size:12px;border:1px solid rgba(224,68,68,.2);background:var(--rd-dim);color:#f8a0a0;flex-shrink:0}
    .banner.warning{border-color:rgba(240,180,41,.22);background:var(--ye-dim);color:#fcd97a}
    .banner.hidden{display:none}

    /* composer */
    .composer{border-top:1px solid var(--b1);background:rgba(7,8,13,.92);backdrop-filter:blur(12px);padding:10px 16px 12px;flex-shrink:0}
    .composer-inner{display:flex;gap:8px;align-items:flex-end}
    .rs-textarea{width:100%;background:var(--s2);border:1px solid var(--b1);border-radius:10px;padding:10px 13px;font:13px/1.45 var(--sans);color:var(--tx);resize:none;outline:none;min-height:42px;max-height:140px;transition:border-color .15s}
    .rs-textarea:focus{border-color:rgba(79,110,247,.4)}
    .rs-textarea::placeholder{color:var(--mu2)}
    .rs-textarea:disabled{opacity:.4;cursor:not-allowed}
    .send-btn{width:40px;height:40px;border-radius:10px;background:var(--ac);border:none;cursor:pointer;display:flex;align-items:center;justify-content:center;color:white;flex-shrink:0;transition:filter .15s,transform .08s,opacity .15s}
    .send-btn:hover:not(:disabled){filter:brightness(1.1)}
    .send-btn:active:not(:disabled){transform:scale(.96)}
    .send-btn:disabled{opacity:.35;cursor:not-allowed}
    .send-btn svg{width:16px;height:16px}
    .composer-meta{display:flex;justify-content:space-between;align-items:center;margin-top:6px;padding:0 2px;font-family:var(--mono);font-size:10px;color:var(--mu2)}

    /* modal */
    .modal-overlay{position:fixed;inset:0;background:rgba(0,0,0,.6);backdrop-filter:blur(4px);z-index:1000;display:flex;align-items:center;justify-content:center;animation:fadein .15s ease}
    @keyframes fadein{from{opacity:0}to{opacity:1}}
    .modal{background:var(--s1);border:1px solid var(--b2);border-radius:16px;padding:24px;width:380px;max-width:calc(100vw - 32px);box-shadow:0 20px 60px rgba(0,0,0,.6)}
    .modal-title{font-size:15px;font-weight:700;margin-bottom:6px}
    .modal-sub{font-size:12px;color:var(--mu3);margin-bottom:20px;line-height:1.5}
    .rs-input{width:100%;background:var(--s2);border:1px solid var(--b1);border-radius:8px;padding:9px 12px;font:13px/1 var(--sans);color:var(--tx);outline:none;transition:border-color .15s;margin-bottom:10px}
    .rs-input:focus{border-color:rgba(79,110,247,.45)}
    .rs-input::placeholder{color:var(--mu2)}
    .modal-actions{display:flex;gap:8px;margin-top:6px}
    .rs-btn{flex:1;padding:9px 14px;border-radius:8px;font:600 12px/1 var(--sans);border:none;cursor:pointer;transition:filter .15s}
    .rs-btn:hover{filter:brightness(1.08)}
    .rs-btn.primary{background:var(--ac);color:white}
    .rs-btn.ghost{background:var(--s3);border:1px solid var(--b2);color:var(--mu3)}
    .rs-btn.ghost:hover{color:var(--tx)}
    .rs-btn.green{background:var(--gn-dim);border:1px solid var(--gn-b);color:var(--gn)}

    /* console */
    .console-wrapper{display:none;align-items:center;background:#0a0b12;border-top:1px solid rgba(79,110,247,.3);padding:5px 14px;gap:10px;font-family:var(--mono);flex-shrink:0}
    .console-wrapper.visible{display:flex}
    .console-prompt{color:var(--ac);font-size:13px;user-select:none}
    #consoleInput{flex:1;background:transparent;border:none;color:var(--tx);font-size:12px;outline:none;font-family:var(--mono)}
    #consoleInput::placeholder{color:var(--mu)}
    .console-hint{font-size:10px;color:var(--mu2);flex-shrink:0}

    /* context menu */
    .rs-ctx-menu{position:fixed;background:#1a1b2e;border:1px solid rgba(79,110,247,.25);border-radius:8px;padding:4px 0;z-index:9999;min-width:160px;box-shadow:0 8px 24px rgba(0,0,0,.6)}
    .rs-ctx-item{padding:7px 14px;color:var(--tx);font-size:12px;cursor:pointer;display:flex;align-items:center;gap:8px;transition:background .1s}
    .rs-ctx-item:hover{background:rgba(79,110,247,.15)}
    .rs-ctx-sep{height:1px;background:var(--b1);margin:3px 0}

    /* toast */
    .rs-toast{position:fixed;bottom:72px;left:50%;transform:translateX(-50%);background:#1a1b2e;border:1px solid rgba(79,110,247,.35);color:var(--tx);padding:7px 16px;border-radius:8px;font-size:11px;font-family:var(--mono);z-index:10000;pointer-events:none;animation:msg-in .15s ease}

    .hidden{display:none!important}
    @media(max-width:900px){
      :root{--rail:0px;--sidebar:200px}
      .rail{display:none}
    }
  </style>
</head>
<body>
<div class="shell">

  <!-- RAIL -->
  <nav class="rail" id="rail">
    <!-- DM button -->
    <div class="rail-item dm-btn active" id="railDmBtn" data-type="dm" title="">
      💬
      <span class="rail-badge hidden" id="dmBadge">0</span>
      <span class="rail-tooltip">Direktmeddelanden</span>
    </div>
    <div class="rail-sep"></div>
    <!-- Server icons injected here -->
    <div id="railServers"></div>
    <div class="rail-sep"></div>
    <div class="rail-item add-btn" id="railAddBtn" title="">
      +
      <span class="rail-tooltip">Skapa server</span>
    </div>
  </nav>

  <!-- SIDEBAR -->
  <aside class="sidebar" id="sidebar">
    <!-- Header changes based on context -->
    <div class="sidebar-header" id="sidebarHeader">
      <div class="sidebar-title">
        <span id="sidebarTitle">Direktmeddelanden</span>
        <button class="icon-btn" id="newDmBtn" title="Ny DM">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
        </button>
      </div>
      <div class="sidebar-sub" id="sidebarSub">Privata konversationer</div>
    </div>

    <div class="sidebar-scroll" id="sidebarScroll">
      <!-- DM search -->
      <div style="padding:8px 10px" id="dmSearchWrap">
        <input class="rs-search" id="searchConv" placeholder="Sök konversationer…" autocomplete="off"/>
      </div>

      <!-- DM list -->
      <div id="dmList"></div>

      <!-- Server channels -->
      <div id="serverChannels" class="hidden"></div>
    </div>

    <!-- Me block -->
    <div class="me-block">
      <div class="av" id="meAvatar">?</div>
      <div class="me-info">
        <div class="me-name" id="meName">Laddar…</div>
        <div class="me-status" id="meStatus">Ansluter…</div>
      </div>
      <div id="connectionStatus" class="conn-pill warning" style="margin-left:auto">
        <div class="conn-dot"></div>
        <span id="connectionText">…</span>
      </div>
    </div>
  </aside>

  <!-- MAIN -->
  <section class="main" id="main">
    <div class="chat-header" id="chatHeader">
      <div class="chat-header-left">
        <div class="chat-peer-av" id="chatPeerAv">—</div>
        <div>
          <div style="display:flex;align-items:center;gap:8px">
            <span class="chat-peer-name" id="chatTitle">RunSpace</span>
            <span class="privacy-tag dm hidden" id="privacyTag"></span>
          </div>
          <div class="chat-peer-sub" id="chatSub">Välj en konversation</div>
        </div>
      </div>
      <div class="chat-actions">
        <button class="icon-btn" id="reconnectBtn" title="Återanslut">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 11-2.12-9.36L23 10"/></svg>
        </button>
      </div>
    </div>

    <div class="banner hidden" id="errorBanner"></div>

    <div class="messages" id="messages">
      <!-- Welcome screen -->
      <div class="welcome-screen" id="welcomeScreen">
        <div class="welcome-icon">🚀</div>
        <div class="welcome-title">Välkommen till RunSpace</div>
        <div class="welcome-sub">Starta en privat konversation, gå med i en grupp eller skapa en server för din community.</div>
        <div class="welcome-actions">
          <button class="w-btn primary" id="wStartDm">💬 Starta DM</button>
          <button class="w-btn ghost" id="wJoinGroup">👥 Gå med i grupp</button>
          <button class="w-btn green" id="wCreateServer">＋ Skapa server</button>
        </div>
      </div>
    </div>

    <div class="typing-row" id="typingArea" style="padding:0 20px;flex-shrink:0;position:relative;z-index:1"></div>

    <!-- Console -->
    <div class="console-wrapper" id="chatConsole">
      <span class="console-prompt">❯</span>
      <input type="text" id="consoleInput" placeholder="/private dm användarnamn" autocomplete="off" spellcheck="false"/>
      <span class="console-hint" id="consoleHint"></span>
    </div>

    <div class="composer" id="composer">
      <div class="composer-inner">
        <textarea id="msg" class="rs-textarea" placeholder="Skriv ett meddelande…" rows="1" disabled></textarea>
        <button id="sendBtn" class="send-btn" disabled>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/></svg>
        </button>
      </div>
      <div class="composer-meta">
        <span id="composerHint">Ingen mottagare vald.</span>
        <span><span id="charCount">0</span>/2000</span>
      </div>
    </div>
  </section>
</div>

<!-- MODALS -->
<div class="modal-overlay hidden" id="modalOverlay">
  <div class="modal" id="modalBox">
    <div class="modal-title" id="modalTitle">Modal</div>
    <div class="modal-sub" id="modalSub"></div>
    <div id="modalBody"></div>
    <div class="modal-actions" id="modalActions"></div>
  </div>
</div>

<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js"></script>
<script>
'use strict';

// ═══════════════════════════════════════════════════════════
//  CONSTANTS
// ═══════════════════════════════════════════════════════════
const MAX_MSG_LEN     = 2000;
const POLL_MS         = 12000;
const TYPING_DEBOUNCE = 1200;
const TYPING_EXPIRE   = 5000;
const TYPING_SEND_MS  = 3000;
const SCROLL_THRESH   = 80;

// ═══════════════════════════════════════════════════════════
//  DOM
// ═══════════════════════════════════════════════════════════
const $ = id => document.getElementById(id);
const EL = {
  rail:          $('rail'),
  railServers:   $('railServers'),
  railDmBtn:     $('railDmBtn'),
  railAddBtn:    $('railAddBtn'),
  dmBadge:       $('dmBadge'),
  sidebarTitle:  $('sidebarTitle'),
  sidebarSub:    $('sidebarSub'),
  sidebarScroll: $('sidebarScroll'),
  dmSearchWrap:  $('dmSearchWrap'),
  dmList:        $('dmList'),
  serverChannels:$('serverChannels'),
  newDmBtn:      $('newDmBtn'),
  meAvatar:      $('meAvatar'),
  meName:        $('meName'),
  meStatus:      $('meStatus'),
  connStatus:    $('connectionStatus'),
  connText:      $('connectionText'),
  chatPeerAv:    $('chatPeerAv'),
  chatTitle:     $('chatTitle'),
  chatSub:       $('chatSub'),
  privacyTag:    $('privacyTag'),
  banner:        $('errorBanner'),
  messages:      $('messages'),
  typingArea:    $('typingArea'),
  chatConsole:   $('chatConsole'),
  consoleInput:  $('consoleInput'),
  consoleHint:   $('consoleHint'),
  msg:           $('msg'),
  sendBtn:       $('sendBtn'),
  compHint:      $('composerHint'),
  charCount:     $('charCount'),
  welcomeScreen: $('welcomeScreen'),
  reconnectBtn:  $('reconnectBtn'),
  wStartDm:      $('wStartDm'),
  wJoinGroup:    $('wJoinGroup'),
  wCreateServer: $('wCreateServer'),
  modalOverlay:  $('modalOverlay'),
  modalTitle:    $('modalTitle'),
  modalSub:      $('modalSub'),
  modalBody:     $('modalBody'),
  modalActions:  $('modalActions'),
};

// ═══════════════════════════════════════════════════════════
//  STATE
// ═══════════════════════════════════════════════════════════
let me           = null;
let connected    = false;
let unreadTotal  = 0;
let pollingTimer = null;
let lastSyncAt   = 0;

// View: 'dm' | 'server'
let activeView   = 'dm';
let activePeer   = '';        // for DMs
let activeServer = null;      // {groupId, name, ...}
let activeChannel= null;      // {channelId, name, type}

const conversations = new Map();   // peer -> ConvState
const pendingMsgs   = new Map();   // clientId -> {peer, text}
const typingSessions= new Map();   // peer -> Map<user, expireTs>
const servers       = new Map();   // groupId -> server data

let localTypingTimer    = null;
let localTypingActive   = false;
let localTypingSendTimer= null;

// ═══════════════════════════════════════════════════════════
//  UTILITIES
// ═══════════════════════════════════════════════════════════
function esc(s){ return String(s??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#039;'); }
function norm(v){ return String(v||'').trim().toLowerCase(); }
function normText(v){ return String(v||'').replace(/\r\n/g,'\n').trim(); }
function genCid(){ return window.crypto?.randomUUID?.() ?? 'l-'+Date.now()+'-'+Math.random().toString(36).slice(2); }
function initials(u){ return String(u||'').trim()[0]?.toUpperCase()||'?'; }
function debounce(fn,d){ let t; return (...a)=>{ clearTimeout(t); t=setTimeout(()=>fn(...a),d); }; }
function fmtTime(v){ const d=new Date(v); return isNaN(d)?'':d.toLocaleTimeString('sv-SE',{hour:'2-digit',minute:'2-digit'}); }
function fmtDate(v){ const d=new Date(v); if(isNaN(d))return''; const n=new Date(); return d.toDateString()===n.toDateString()?d.toLocaleTimeString('sv-SE',{hour:'2-digit',minute:'2-digit'}):d.toLocaleDateString('sv-SE',{month:'2-digit',day:'2-digit'}); }

async function api(path, opts={}){
  const r = await fetch(path, {credentials:'include', headers:{'Content-Type':'application/json',...(opts.headers||{})}, ...opts});
  if(!r.ok){ const t=await r.text().catch(()=>''); throw new Error(t||`HTTP ${r.status}`); }
  const ct = r.headers.get('content-type')||'';
  return ct.includes('application/json') ? r.json() : r.text();
}
function apiPost(path, body){ return api(path,{method:'POST',body:body?JSON.stringify(body):undefined}); }

function showToast(msg){
  const t=document.createElement('div'); t.className='rs-toast'; t.textContent=msg;
  document.body.appendChild(t); setTimeout(()=>t.remove(),2500);
}

function showBanner(text,type='error'){
  if(!text){ EL.banner.className='banner hidden'; return; }
  EL.banner.className='banner'+(type==='warning'?' warning':'');
  EL.banner.textContent=text;
}

// ═══════════════════════════════════════════════════════════
//  MODAL SYSTEM
// ═══════════════════════════════════════════════════════════
function openModal({title, sub='', body='', actions=[]}){
  EL.modalTitle.textContent = title;
  EL.modalSub.textContent = sub;
  EL.modalBody.innerHTML = body;
  EL.modalActions.innerHTML = '';
  actions.forEach(a=>{
    const btn=document.createElement('button');
    btn.className='rs-btn '+(a.cls||'ghost');
    btn.textContent=a.label;
    btn.addEventListener('click',()=>{ if(a.action) a.action(); if(a.close!==false) closeModal(); });
    EL.modalActions.appendChild(btn);
  });
  EL.modalOverlay.classList.remove('hidden');
}
function closeModal(){ EL.modalOverlay.classList.add('hidden'); }
EL.modalOverlay.addEventListener('click', e=>{ if(e.target===EL.modalOverlay) closeModal(); });

// ═══════════════════════════════════════════════════════════
//  CONVERSATION STATE (DMs)
// ═══════════════════════════════════════════════════════════
function ensureConv(peer){
  const k=norm(peer); if(!k) return null;
  if(!conversations.has(k)) conversations.set(k,{peer:k,messages:[],unread:0,verified:false,loadedHistory:false,loadingHistory:false,historyExhausted:false});
  return conversations.get(k);
}
function getConv(peer){ return conversations.get(norm(peer))||null; }

function mkMsg({id,clientId,from,text,ts,system=false,pending=false,failed=false}){
  return{id:id||null,clientId:clientId||null,from:String(from||''),text:String(text||''),ts:ts||new Date().toISOString(),system:!!system,pending:!!pending,failed:!!failed};
}

function upsertMsg(peer, msg){
  const s=ensureConv(peer); if(!s) return;
  const bi=msg.id?s.messages.findIndex(m=>m.id&&m.id===msg.id):-1;
  const bc=msg.clientId?s.messages.findIndex(m=>m.clientId&&m.clientId===msg.clientId):-1;
  const idx=bi>=0?bi:bc;
  if(idx>=0) s.messages[idx]={...s.messages[idx],...msg};
  else s.messages.push(msg);
  dedupMsgs(peer); sortMsgs(peer);
}

function dedupMsgs(peer){
  const s=getConv(peer); if(!s) return;
  const seen=new Set(),res=[];
  for(const m of s.messages){
    const k=m.id?`id:${m.id}`:m.clientId?`cid:${m.clientId}`:`fb:${m.from}|${m.text}|${m.ts}`;
    if(seen.has(k)) continue; seen.add(k); res.push(m);
  }
  s.messages=res;
}
function sortMsgs(peer){ const s=getConv(peer); if(s) s.messages.sort((a,b)=>new Date(a.ts)-new Date(b.ts)); }

function getMsgs(peer){ return getConv(peer)?.messages||[]; }
function getLastMsg(peer){ const m=getMsgs(peer); return m.length?m[m.length-1]:null; }

// ═══════════════════════════════════════════════════════════
//  UNREAD
// ═══════════════════════════════════════════════════════════
function recalcUnread(){
  unreadTotal=[...conversations.values()].reduce((n,c)=>n+(c.unread||0),0);
  document.title=unreadTotal>0?`(${unreadTotal}) RunSpace`:'RunSpace';
  EL.dmBadge.textContent=unreadTotal>99?'99+':unreadTotal;
  if(unreadTotal>0) EL.dmBadge.classList.remove('hidden');
  else EL.dmBadge.classList.add('hidden');
}

// ═══════════════════════════════════════════════════════════
//  SCROLL
// ═══════════════════════════════════════════════════════════
function nearBottom(){ return EL.messages.scrollHeight-EL.messages.scrollTop-EL.messages.clientHeight<SCROLL_THRESH; }
function scrollBottom(force=false){ if(force||nearBottom()) EL.messages.scrollTop=EL.messages.scrollHeight; }

// ═══════════════════════════════════════════════════════════
//  RENDER — RAIL
// ═══════════════════════════════════════════════════════════
function renderRail(){
  // DM button active state
  EL.railDmBtn.classList.toggle('active', activeView==='dm');

  // Server icons
  EL.railServers.innerHTML='';
  for(const [gid, srv] of servers.entries()){
    const el=document.createElement('div');
    el.className='rail-item'+(activeView==='server'&&activeServer?.groupId===gid?' active':'');
    el.dataset.gid=gid;
    el.innerHTML=`${esc(srv.name[0].toUpperCase())}<span class="rail-tooltip">${esc(srv.name)}</span>`;
    el.style.fontWeight='700';
    el.style.fontSize='15px';
    el.addEventListener('click',()=>switchToServer(gid));
    EL.railServers.appendChild(el);
  }
}

// ═══════════════════════════════════════════════════════════
//  RENDER — SIDEBAR (DM mode)
// ═══════════════════════════════════════════════════════════
function renderDmSidebar(){
  EL.sidebarTitle.textContent='Direktmeddelanden';
  EL.sidebarSub.textContent='Privata konversationer';
  EL.dmSearchWrap.classList.remove('hidden');
  EL.newDmBtn.classList.remove('hidden');
  EL.dmList.classList.remove('hidden');
  EL.serverChannels.classList.add('hidden');

  const search=norm(document.getElementById('searchConv')?.value||'');
  const entries=[...conversations.entries()]
    .filter(([p])=>!search||p.includes(search))
    .sort((a,b)=>new Date(getLastMsg(b[0])?.ts||0)-new Date(getLastMsg(a[0])?.ts||0));

  if(!entries.length){
    EL.dmList.innerHTML='<div style="padding:12px 14px;font-size:11px;color:var(--mu2);font-family:var(--mono)">Inga konversationer ännu.</div>';
    return;
  }

  EL.dmList.innerHTML=entries.map(([peer,data])=>{
    const last=getLastMsg(peer);
    const preview=last?(last.system?last.text:`${last.from===me?.username?'Du':last.from}: ${last.text}`):'Ingen historik.';
    const unread=data.unread>0?`<div class="unread-badge">${data.unread>99?'99+':data.unread}</div>`:'';
    return`<div class="conv-item${peer===activePeer?' active':''}" data-peer="${esc(peer)}">
      <div class="conv-av">${esc(initials(peer))}</div>
      <div class="conv-info">
        <div class="conv-name">@${esc(peer)}</div>
        <div class="conv-preview">${esc(preview)}</div>
      </div>
      <div class="conv-meta">
        <div class="conv-time">${esc(fmtDate(last?.ts||''))}</div>
        ${unread}
      </div>
    </div>`;
  }).join('');

  EL.dmList.querySelectorAll('.conv-item').forEach(item=>{
    item.addEventListener('click',()=>activateDm(item.dataset.peer));
    item.addEventListener('contextmenu',e=>{ e.preventDefault(); showConvCtxMenu(e, item.dataset.peer); });
  });
}

// ═══════════════════════════════════════════════════════════
//  RENDER — SIDEBAR (Server mode)
// ═══════════════════════════════════════════════════════════
function renderServerSidebar(srv){
  EL.sidebarTitle.textContent=srv.name;
  EL.sidebarSub.textContent=`${(srv.members||[]).length} medlemmar`;
  EL.dmSearchWrap.classList.add('hidden');
  EL.newDmBtn.classList.add('hidden');
  EL.dmList.classList.add('hidden');
  EL.serverChannels.classList.remove('hidden');

  const channels=srv.channels||[];
  const textCh=channels.filter(c=>c.type==='text');
  const voiceCh=channels.filter(c=>c.type==='voice');

  let html='';
  if(textCh.length){
    html+=`<div class="section-label">Textkanaler</div>`;
    html+=textCh.map(ch=>`
      <div class="ch-item${activeChannel?.channelId===ch.channelId?' active':''}" data-cid="${esc(ch.channelId)}" data-cname="${esc(ch.name)}">
        <span class="ch-icon">#</span>
        <span class="ch-name">${esc(ch.name)}</span>
      </div>`).join('');
  }
  if(voiceCh.length){
    html+=`<div class="section-label" style="margin-top:8px">Röstkanaler</div>`;
    html+=voiceCh.map(ch=>`
      <div class="ch-item" data-cid="${esc(ch.channelId)}" data-cname="${esc(ch.name)}" data-voice="1">
        <span class="ch-icon">🔊</span>
        <span class="ch-name">${esc(ch.name)}</span>
      </div>`).join('');
  }

  EL.serverChannels.innerHTML=html;
  EL.serverChannels.querySelectorAll('.ch-item').forEach(item=>{
    if(item.dataset.voice) return;
    item.addEventListener('click',()=>activateChannel(item.dataset.cid, item.dataset.cname));
  });
}

// ═══════════════════════════════════════════════════════════
//  RENDER — CHAT HEADER
// ═══════════════════════════════════════════════════════════
function renderChatHeader(){
  if(activeView==='dm'&&activePeer){
    EL.chatPeerAv.textContent=initials(activePeer);
    EL.chatTitle.textContent='@'+activePeer;
    EL.chatSub.textContent='Direktmeddelanden — '+( connected?'ansluten':'frånkopplad');
    EL.privacyTag.className='privacy-tag dm';
    EL.privacyTag.textContent='🔒 Privat';
    EL.privacyTag.classList.remove('hidden');
  } else if(activeView==='server'&&activeChannel){
    EL.chatPeerAv.textContent='#';
    EL.chatTitle.textContent='#'+activeChannel.name;
    EL.chatSub.textContent=(activeServer?.name||'')+'  •  server';
    EL.privacyTag.className='privacy-tag server';
    EL.privacyTag.textContent='🌐 Server';
    EL.privacyTag.classList.remove('hidden');
  } else {
    EL.chatPeerAv.textContent='—';
    EL.chatTitle.textContent='RunSpace';
    EL.chatSub.textContent='Välj en konversation';
    EL.privacyTag.classList.add('hidden');
  }
}

function updateComposerState(){
  const active=(activeView==='dm'&&activePeer)||(activeView==='server'&&activeChannel);
  EL.msg.disabled=!(me&&active);
  const t=normText(EL.msg.value);
  EL.sendBtn.disabled=!(me&&active&&t.length>0&&t.length<=MAX_MSG_LEN);
  if(!me) EL.compHint.textContent='Du måste vara inloggad.';
  else if(activeView==='dm'&&!activePeer) EL.compHint.textContent='Välj en konversation.';
  else if(activeView==='server'&&!activeChannel) EL.compHint.textContent='Välj en kanal.';
  else if(!connected) EL.compHint.textContent='Realtime frånkopplad.';
  else if(activeView==='dm') EL.compHint.textContent='Skickar till @'+activePeer;
  else EL.compHint.textContent='#'+activeChannel.name;
}

// ═══════════════════════════════════════════════════════════
//  RENDER — MESSAGES
// ═══════════════════════════════════════════════════════════
function shouldGroup(prev, next){
  if(!prev||!next||prev.system||next.system||prev.from!==next.from) return false;
  return new Date(next.ts)-new Date(prev.ts)<120000;
}
function shouldDateDivider(prev,next){
  if(!prev||!next) return false;
  return new Date(prev.ts).toDateString()!==new Date(next.ts).toDateString();
}

function renderMsgNode(msg, prevMsg){
  const cls=msg.system?'system':msg.from===me?.username?'mine':'other';
  const grouped=shouldGroup(prevMsg,msg);
  const div=document.createElement('div');
  div.className='msg-row '+cls;
  if(msg.id) div.dataset.msgId=msg.id;
  if(msg.clientId) div.dataset.clientId=msg.clientId;
  if(!grouped){
    const label=msg.system?'System':msg.from===me?.username?'Du':msg.from;
    const meta=document.createElement('div');
    meta.className='msg-meta';
    let statusHtml='';
    if(!msg.system&&msg.from===me?.username){
      const cls2=msg.failed?'failed':msg.pending?'pending':'sent';
      const txt=msg.failed?'✕':msg.pending?'…':'✓';
      statusHtml=`<span class="msg-status ${cls2}">${txt}</span>`;
    }
    meta.innerHTML=`<span>${esc(label)} · ${esc(fmtTime(msg.ts))}</span>${statusHtml}`;
    if(msg.failed){
      const st=meta.querySelector('.msg-status');
      if(st) st.addEventListener('click',()=>retryMsg(activePeer,msg.clientId));
    }
    div.appendChild(meta);
  }
  const bubble=document.createElement('div');
  bubble.className='msg-bubble';
  // Image/file links
  if(msg.text.startsWith('[bild] /uploads/')||msg.text.startsWith('[bild] http')){
    const url=msg.text.replace('[bild] ','');
    bubble.innerHTML=`<img src="${esc(url)}" style="max-width:100%;max-height:260px;border-radius:8px;display:block" loading="lazy"/>`;
  } else if(msg.text.startsWith('[fil] ')){
    const url=msg.text.replace('[fil] ','');
    const name=url.split('/').pop();
    bubble.innerHTML=`<a href="${esc(url)}" target="_blank" style="color:inherit;display:flex;align-items:center;gap:8px">📎 ${esc(name)}</a>`;
  } else {
    bubble.textContent=msg.text;
  }
  div.appendChild(bubble);
  return div;
}

function renderMsgs(peer){
  clearMsgs();
  const msgs=getMsgs(peer);
  if(!msgs.length){ renderWelcome(false); return; }
  const frag=document.createDocumentFragment();
  msgs.forEach((m,i)=>{
    if(i>0&&shouldDateDivider(msgs[i-1],m)){
      const d=document.createElement('div'); d.className='date-divider';
      d.textContent=new Date(m.ts).toLocaleDateString('sv-SE',{weekday:'long',month:'long',day:'numeric'});
      frag.appendChild(d);
    }
    frag.appendChild(renderMsgNode(m,msgs[i-1]||null));
  });
  EL.messages.appendChild(frag);
  scrollBottom(true);
}

function renderServerMsgs(msgs){
  clearMsgs();
  if(!msgs.length){ renderWelcome(false); return; }
  const frag=document.createDocumentFragment();
  msgs.forEach((m,i)=>{
    const msg=mkMsg({id:m.id,from:m.from,text:m.text,ts:m.ts});
    frag.appendChild(renderMsgNode(msg,i>0?mkMsg({from:msgs[i-1].from,ts:msgs[i-1].ts}):null));
  });
  EL.messages.appendChild(frag);
  scrollBottom(true);
}

function appendMsgNode(msg){
  const es=EL.messages.querySelector('.empty-state,.welcome-screen');
  if(es) es.remove();
  const msgs=getMsgs(activeView==='dm'?activePeer:'');
  const prev=msgs.length>=2?msgs[msgs.length-2]:null;
  if(prev&&shouldDateDivider(prev,msg)){
    const d=document.createElement('div'); d.className='date-divider';
    d.textContent=new Date(msg.ts).toLocaleDateString('sv-SE',{weekday:'long',month:'long',day:'numeric'});
    EL.messages.appendChild(d);
  }
  const wasNear=nearBottom();
  EL.messages.appendChild(renderMsgNode(msg,prev));
  if(wasNear) scrollBottom(true);
}

function appendServerMsgNode(msg){
  const wasNear=nearBottom();
  EL.messages.appendChild(renderMsgNode(mkMsg({id:msg.id,from:msg.from,text:msg.text,ts:msg.ts}),null));
  if(wasNear) scrollBottom(true);
}

function clearMsgs(){ EL.messages.innerHTML=''; }

function renderWelcome(show=true){
  clearMsgs();
  if(!show) return;
  const ws=document.createElement('div'); ws.className='welcome-screen';
  ws.innerHTML=`<div class="welcome-icon">🚀</div><div class="welcome-title">Välkommen till RunSpace</div><div class="welcome-sub">Starta en privat konversation, gå med i en grupp eller skapa en server.</div><div class="welcome-actions"><button class="w-btn primary" onclick="showNewDmModal()">💬 Starta DM</button><button class="w-btn green" onclick="showCreateServerModal()">＋ Skapa server</button></div>`;
  EL.messages.appendChild(ws);
}

function patchMsgNode(clientId, patch){
  const node=EL.messages.querySelector(`[data-client-id="${CSS.escape(clientId)}"]`);
  if(!node) return;
  if(patch.id) node.dataset.msgId=patch.id;
  const st=node.querySelector('.msg-status');
  if(!st) return;
  if(patch.failed){ st.className='msg-status failed'; st.textContent='✕'; st.onclick=()=>retryMsg(activePeer,clientId); }
  else if(patch.pending===false){ st.className='msg-status sent'; st.textContent='✓'; st.onclick=null; }
}

// ═══════════════════════════════════════════════════════════
//  TYPING
// ═══════════════════════════════════════════════════════════
function setTyping(peer,user,on){ if(!typingSessions.has(peer))typingSessions.set(peer,new Map()); const m=typingSessions.get(peer); if(on) m.set(user,Date.now()+TYPING_EXPIRE); else m.delete(user); }
function renderTyping(peer){ const m=typingSessions.get(peer)||new Map(); const now=Date.now(); const a=[...m.entries()].filter(([,e])=>e>now).map(([u])=>u); if(!a.length){EL.typingArea.innerHTML='';return;} const label=a.length===1?`${esc(a[0])} skriver`:`${a.length} skriver`; EL.typingArea.innerHTML=`<div class="typing-row"><div class="typing-indicator"><div class="typing-dot"></div><div class="typing-dot"></div><div class="typing-dot"></div></div><span class="typing-label">${label}…</span></div>`; }
function startLocalTyping(peer){ if(!connected||!peer) return; if(!localTypingActive){localTypingActive=true;connection.invoke('SendTyping',peer).catch(()=>{}); localTypingSendTimer=setInterval(()=>{if(localTypingActive)connection.invoke('SendTyping',peer).catch(()=>{});},TYPING_SEND_MS);} clearTimeout(localTypingTimer); localTypingTimer=setTimeout(()=>stopLocalTyping(peer),TYPING_DEBOUNCE+500); }
function stopLocalTyping(peer){ if(!localTypingActive)return; localTypingActive=false; clearTimeout(localTypingTimer); clearInterval(localTypingSendTimer); }
setInterval(()=>{ const now=Date.now(); for(const[peer,m]of typingSessions.entries()){let ch=false;for(const[u,e]of m.entries())if(e<=now){m.delete(u);ch=true;}if(ch&&peer===activePeer)renderTyping(peer);}},2000);

// ═══════════════════════════════════════════════════════════
//  HISTORY
// ═══════════════════════════════════════════════════════════
async function loadDmHistory(peer){
  const s=ensureConv(peer); if(!s||s.loadedHistory||s.loadingHistory) return;
  s.loadingHistory=true;
  try{
    const h=await api(`/api/chat/history/${encodeURIComponent(peer)}`);
    if(h&&Array.isArray(h.messages)){
      const incoming=h.messages.map(m=>mkMsg({id:m.id,from:m.fromUsername||m.from,text:m.text,ts:m.sentAtUtc||m.ts}));
      const pending=s.messages.filter(m=>m.pending||m.failed);
      s.messages=[];
      [...incoming,...pending].forEach(m=>upsertMsg(peer,m));
      s.verified=!!h.verified;
      s.loadedHistory=true;
    }
  }catch(e){ console.error('[history]',e); }
  finally{ s.loadingHistory=false; }
}

async function loadServerChannelHistory(groupId, channelId){
  try{
    const msgs=await api(`/api/groups/${encodeURIComponent(groupId)}/channels/${encodeURIComponent(channelId)}/history`);
    return Array.isArray(msgs)?msgs:[];
  }catch(e){ console.error('[ch history]',e); return []; }
}

// ═══════════════════════════════════════════════════════════
//  CONVERSATION SUMMARIES
// ═══════════════════════════════════════════════════════════
async function refreshConversations(){
  if(!me) return;
  try{
    const list=await api('/api/chat/conversations');
    if(Array.isArray(list)){
      for(const item of list){
        const peer=norm(item.peer||item.username); if(!peer) continue;
        const s=ensureConv(peer);
        s.unread=Number(item.unreadCount||0);
        if((item.text||item.lastMessageText)&&!s.messages.length){
          upsertMsg(peer,mkMsg({id:item.id||item.lastMessageId,from:item.from||item.lastMessageFrom||peer,text:item.text||item.lastMessageText,ts:item.ts||item.lastMessageAt||new Date().toISOString()}));
        }
      }
      recalcUnread();
      if(activeView==='dm') renderDmSidebar();
    }
  }catch(e){ console.error('[conversations]',e); }
}

async function loadServers(){
  if(!me) return;
  try{
    const list=await api('/api/groups');
    servers.clear();
    for(const g of list){
      // Load full group details
      try{
        const full=await api(`/api/groups/${encodeURIComponent(g.groupId)}`);
        servers.set(g.groupId, full);
      }catch{ servers.set(g.groupId, g); }
    }
    renderRail();
  }catch(e){ console.error('[servers]',e); }
}

// ═══════════════════════════════════════════════════════════
//  ACTIVATION
// ═══════════════════════════════════════════════════════════
function switchToDm(){
  activeView='dm'; activeServer=null; activeChannel=null;
  EL.railDmBtn.classList.add('active');
  renderRail(); renderDmSidebar();
  if(activePeer){ renderChatHeader(); renderMsgs(activePeer); updateComposerState(); }
  else{ renderChatHeader(); renderWelcome(); updateComposerState(); }
}

async function switchToServer(groupId){
  const srv=servers.get(groupId); if(!srv) return;
  activeView='server'; activeServer=srv; activeChannel=null; activePeer='';
  renderRail(); renderServerSidebar(srv); renderChatHeader();
  clearMsgs(); renderWelcome(false);
  updateComposerState();
  // Join group hub
  if(connected&&srv.channels?.length){
    const firstText=srv.channels.find(c=>c.type==='text');
    if(firstText) await activateChannel(firstText.channelId, firstText.name);
  }
}

async function activateChannel(channelId, channelName){
  if(!activeServer) return;
  activeChannel={channelId, name:channelName};
  try{ await connection.invoke('JoinGroupChannel', activeServer.groupId, channelId); }catch(e){ console.warn(e); }
  renderServerSidebar(activeServer);
  renderChatHeader(); updateComposerState();
  const msgs=await loadServerChannelHistory(activeServer.groupId, channelId);
  renderServerMsgs(msgs);
}

async function activateDm(peer){
  const np=norm(peer); if(!np) return;
  activePeer=np; activeView='dm';
  const s=ensureConv(np);
  s.unread=0; recalcUnread();
  renderDmSidebar(); renderChatHeader(); updateComposerState();
  if(connected) connection.invoke('JoinPrivate',np).catch(()=>{});
  await loadDmHistory(np);
  renderMsgs(np);
  setTimeout(()=>{ if(!EL.msg.disabled) EL.msg.focus(); },0);
}

// ═══════════════════════════════════════════════════════════
//  SEND
// ═══════════════════════════════════════════════════════════
async function sendMessage(){
  const text=normText(EL.msg.value);
  if(!me||!text||text.length>MAX_MSG_LEN) return;
  showBanner('');
  EL.msg.value=''; updateCharCount();

  if(activeView==='dm'&&activePeer){
    const peer=activePeer;
    const clientId=genCid();
    const msg=mkMsg({clientId,from:me.username,text,ts:new Date().toISOString(),pending:true});
    upsertMsg(peer,msg); pendingMsgs.set(clientId,{peer,text});
    appendMsgNode(msg);
    renderDmSidebar();
    stopLocalTyping(peer);
    try{
      await connection.invoke('SendMessage',{to:peer,text,iv:'',encryptedKey:'',senderEncryptedKey:'',algorithm:'plain',encrypted:0,recipientKeys:[],senderKeys:[],replyToId:0});
      const upd={pending:false,failed:false}; const s=getConv(peer); if(s){const i=s.messages.findIndex(m=>m.clientId===clientId);if(i>=0)s.messages[i]={...s.messages[i],...upd};} pendingMsgs.delete(clientId); patchMsgNode(clientId,upd);
    }catch(e){
      const upd={pending:false,failed:true}; const s=getConv(peer); if(s){const i=s.messages.findIndex(m=>m.clientId===clientId);if(i>=0)s.messages[i]={...s.messages[i],...upd};} patchMsgNode(clientId,upd);
      showBanner('Meddelandet kunde inte skickas.','warning');
    }
  } else if(activeView==='server'&&activeServer&&activeChannel){
    try{
      await connection.invoke('SendGroupMessage',activeServer.groupId,activeChannel.channelId,text);
    }catch(e){ showBanner('Kunde inte skicka: '+e.message,'warning'); }
  }
}

async function retryMsg(peer, clientId){
  const s=getConv(peer); if(!s) return;
  const msg=s.messages.find(m=>m.clientId===clientId); if(!msg||!msg.failed) return;
  const i=s.messages.indexOf(msg); s.messages[i]={...msg,pending:true,failed:false};
  patchMsgNode(clientId,{pending:true,failed:false});
  pendingMsgs.set(clientId,{peer,text:msg.text});
  try{
    await connection.invoke('SendMessage',{to:peer,text:msg.text,iv:'',encryptedKey:'',senderEncryptedKey:'',algorithm:'plain',encrypted:0,recipientKeys:[],senderKeys:[],replyToId:0});
    s.messages[i]={...s.messages[i],pending:false,failed:false}; pendingMsgs.delete(clientId); patchMsgNode(clientId,{pending:false,failed:false});
  }catch(e){ s.messages[i]={...s.messages[i],pending:false,failed:true}; patchMsgNode(clientId,{failed:true}); }
}

// ═══════════════════════════════════════════════════════════
//  REALTIME
// ═══════════════════════════════════════════════════════════
const connection=new signalR.HubConnectionBuilder()
  .withUrl('/ws/chat',{withCredentials:true})
  .withAutomaticReconnect([0,2000,5000,10000,15000])
  .build();

function registerHandlers(){
  connection.on('ReceiveMessage', payload=>{
    if(!payload?.fromUsername&&!payload?.from) return;
    const from=payload.fromUsername||payload.from;
    const to=payload.toUsername||payload.to;
    if(!from||!to) return;
    const peer=from===me?.username?to:from;
    const msg=mkMsg({id:payload.id,from,text:payload.text,ts:payload.sentAtUtc||payload.ts});

    // match pending
    for(const[cid,p]of pendingMsgs.entries()){
      if(p.peer===peer&&p.text===msg.text){ msg.clientId=cid; pendingMsgs.delete(cid); break; }
    }

    upsertMsg(peer,msg);
    const isIncoming=from!==me?.username;
    if(isIncoming&&peer!==activePeer){
      const s=ensureConv(peer); s.unread=(s.unread||0)+1;
      if(Notification.permission==='granted'&&document.hidden) try{new Notification('@'+from,{body:(payload.text||'').slice(0,100)});}catch{}
    }
    if(peer===activePeer&&activeView==='dm') appendMsgNode(msg);
    renderDmSidebar(); recalcUnread();
  });

  connection.on('ReceivePrivateMessage', payload=>{
    // alias
    if(payload) connection.emit?.('ReceiveMessage',{...payload});
    else return;
    if(!payload?.fromUsername) return;
    const from=payload.fromUsername; const to=payload.toUsername;
    const peer=from===me?.username?to:from;
    const msg=mkMsg({id:payload.id,from,text:payload.text,ts:payload.sentAtUtc||new Date().toISOString()});
    for(const[cid,p]of pendingMsgs.entries()){if(p.peer===peer&&p.text===msg.text){msg.clientId=cid;pendingMsgs.delete(cid);break;}}
    upsertMsg(peer,msg);
    if(from!==me?.username&&peer!==activePeer){const s=ensureConv(peer);s.unread=(s.unread||0)+1;}
    if(peer===activePeer&&activeView==='dm') appendMsgNode(msg);
    renderDmSidebar(); recalcUnread();
  });

  connection.on('ReceiveGroupMessage', payload=>{
    if(!payload) return;
    if(activeView==='server'&&activeServer?.groupId===payload.groupId&&activeChannel?.channelId===payload.channelId){
      appendServerMsgNode(payload);
    }
  });

  connection.on('ReceiveTyping', user=>{
    if(!user||user===me?.username) return;
    setTyping(activePeer,user,true);
    if(activeView==='dm') renderTyping(activePeer);
    setTimeout(()=>{ setTyping(activePeer,user,false); if(activeView==='dm') renderTyping(activePeer); },TYPING_EXPIRE);
  });
}

async function startRealtime(){
  setConnState('warning','Ansluter…');
  try{
    await connection.start();
    connected=true; setConnState('connected','Ansluten'); showBanner('');
  }catch(e){
    connected=false; setConnState('error','Misslyckades'); showBanner('Realtime misslyckades.','warning');
  }
  updateComposerState();
}

connection.onreconnecting(()=>{ connected=false; setConnState('warning','Återansluter…'); updateComposerState(); showBanner('Återansluter…','warning'); });
connection.onreconnected(async()=>{ connected=true; setConnState('connected','Ansluten'); updateComposerState(); showBanner(''); await refreshConversations(); if(activePeer&&activeView==='dm'){ await loadDmHistory(activePeer); renderMsgs(activePeer); } });
connection.onclose(()=>{ connected=false; setConnState('error','Frånkopplad'); updateComposerState(); showBanner('Frånkopplad. Försök återansluta.','warning'); });

function setConnState(state,text){ EL.connStatus.className='conn-pill '+state; EL.connText.textContent=text; }

// ═══════════════════════════════════════════════════════════
//  CONTEXT MENU
// ═══════════════════════════════════════════════════════════
let activeCtxMenu=null;
function removeCtx(){ if(activeCtxMenu){activeCtxMenu.remove();activeCtxMenu=null;} }
document.addEventListener('click',removeCtx);
document.addEventListener('keydown',e=>{ if(e.key==='Escape'){ removeCtx(); closeConsole(); } });

function showCtxMenu(x,y,items){
  removeCtx();
  const menu=document.createElement('div'); menu.className='rs-ctx-menu';
  items.forEach(item=>{
    if(item==='sep'){ const s=document.createElement('div'); s.className='rs-ctx-sep'; menu.appendChild(s); return; }
    const el=document.createElement('div'); el.className='rs-ctx-item'+(item.danger?' danger':'');
    el.innerHTML=`${item.icon||''} ${esc(item.label)}`;
    el.addEventListener('click',e=>{ e.stopPropagation(); removeCtx(); item.action(); });
    menu.appendChild(el);
  });
  document.body.appendChild(menu);
  const r=menu.getBoundingClientRect();
  menu.style.left=Math.min(x,window.innerWidth-r.width-8)+'px';
  menu.style.top=Math.min(y,window.innerHeight-r.height-8)+'px';
  activeCtxMenu=menu;
}

function showConvCtxMenu(e, peer){
  showCtxMenu(e.clientX,e.clientY,[
    {icon:'💬',label:'Öppna DM',action:()=>activateDm(peer)},
    'sep',
    {icon:'📋',label:'Kopiera User ID',action:async()=>{
      try{ const d=await api(`/api/profile/public/${encodeURIComponent(peer)}`); if(d.publicId){await navigator.clipboard.writeText(d.publicId);showToast('User ID kopierat');} }catch{}
    }},
    {icon:'🔗',label:'Kopiera användarnamn',action:async()=>{ try{await navigator.clipboard.writeText('@'+peer);showToast('Kopierat');}catch{} }},
  ]);
}

// ═══════════════════════════════════════════════════════════
//  CONSOLE
// ═══════════════════════════════════════════════════════════
function openConsole(){ EL.chatConsole.classList.add('visible'); EL.consoleInput.value=''; EL.consoleHint.textContent=''; EL.consoleInput.focus(); }
function closeConsole(){ EL.chatConsole.classList.remove('visible'); EL.consoleInput.value=''; EL.consoleHint.textContent=''; setTimeout(()=>{if(!EL.msg.disabled)EL.msg.focus();},0); }

EL.msg.addEventListener('keydown',e=>{ if(e.key==='/'&&EL.msg.value===''){e.preventDefault();openConsole();} });
EL.consoleInput.addEventListener('input',()=>{
  const v=EL.consoleInput.value;
  if(v.startsWith('/private dm ')) EL.consoleHint.textContent='→ DM med @'+(v.slice(12).trim()||'?');
  else if(v.startsWith('/')) EL.consoleHint.textContent=v.length>1?'':'';
  else EL.consoleHint.textContent='';
});
EL.consoleInput.addEventListener('keydown',async e=>{
  if(e.key==='Escape'){closeConsole();return;}
  if(e.key==='Enter'){
    const raw=EL.consoleInput.value.trim(); closeConsole();
    await handleConsoleCmd(raw);
  }
});

async function handleConsoleCmd(input){
  if(!input.startsWith('/')) return;
  const parts=input.split(/\s+/); const cmd=parts[0].toLowerCase();
  if(cmd==='/private'&&parts[1]?.toLowerCase()==='dm'){
    const u=parts.slice(2).join('').trim().toLowerCase();
    if(!u){showBanner('Ange användarnamn: /private dm {användare}','warning');return;}
    switchToDm(); await activateDm(u); return;
  }
  showBanner(`Okänt kommando: ${cmd}`,'warning');
}

// ═══════════════════════════════════════════════════════════
//  MODALS
// ═══════════════════════════════════════════════════════════
function showNewDmModal(){
  openModal({
    title:'Starta DM',
    sub:'Ange användarnamnet på den du vill chatta med.',
    body:`<input class="rs-input" id="dmTargetInput" placeholder="Användarnamn…" autocomplete="off"/>`,
    actions:[
      {label:'Öppna',cls:'primary',close:false,action:async()=>{
        const u=norm(document.getElementById('dmTargetInput')?.value||'');
        if(!u){return;}
        closeModal(); switchToDm(); await activateDm(u);
      }},
      {label:'Avbryt',cls:'ghost'}
    ]
  });
  setTimeout(()=>document.getElementById('dmTargetInput')?.focus(),100);
}

function showCreateServerModal(){
  openModal({
    title:'Skapa server',
    sub:'Ge din server ett namn. Du kan lägga till kanaler och bjuda in folk efteråt.',
    body:`<input class="rs-input" id="serverNameInput" placeholder="Servernamn…" maxlength="32" autocomplete="off"/><input class="rs-input" id="serverDescInput" placeholder="Beskrivning (valfritt)…" maxlength="200" autocomplete="off"/>`,
    actions:[
      {label:'Skapa',cls:'primary',close:false,action:async()=>{
        const name=document.getElementById('serverNameInput')?.value.trim();
        const desc=document.getElementById('serverDescInput')?.value.trim()||'';
        if(!name) return;
        try{
          const res=await apiPost('/api/groups',{name,description:desc});
          closeModal(); await loadServers(); await switchToServer(res.groupId);
        }catch(e){ showBanner('Kunde inte skapa server: '+e.message,'warning'); }
      }},
      {label:'Avbryt',cls:'ghost'}
    ]
  });
  setTimeout(()=>document.getElementById('serverNameInput')?.focus(),100);
}

// ═══════════════════════════════════════════════════════════
//  PASTE UPLOAD
// ═══════════════════════════════════════════════════════════
const BLOCKED_EXTS=new Set(['.exe','.bat','.cmd','.msi','.scr','.ps1','.vbs','.dll','.sh','.php','.jar','.app','.apk']);

document.addEventListener('paste',async e=>{
  if(!me||!(activePeer||activeChannel)) return;
  const items=e.clipboardData?.items; if(!items) return;
  let file=null;
  for(const item of items){ if(item.kind==='file'){file=item.getAsFile();break;} }
  if(!file) return;
  e.preventDefault();
  const ext='.'+file.name.split('.').pop().toLowerCase();
  if(BLOCKED_EXTS.has(ext)){showBanner('Filtypen '+ext+' är inte tillåten.','warning');return;}
  const isImg=file.type.startsWith('image/');
  const endpoint=isImg?'/api/chat/upload-image':'/api/chat/upload-file';
  const field=isImg?'image':'file';
  showToast('Laddar upp…');
  try{
    const fd=new FormData(); fd.append(field,file,file.name);
    const res=await fetch(endpoint,{method:'POST',credentials:'include',body:fd});
    if(!res.ok){ const err=await res.json().catch(()=>({error:'Fel'})); showBanner(err.error||'Uppladdning misslyckades.','warning'); return; }
    const data=await res.json();
    const url=data.imageUrl||data.fileUrl;
    const msgText=(isImg?'[bild] ':'[fil] ')+url;
    if(activeView==='dm'&&activePeer){
      await connection.invoke('SendMessage',{to:activePeer,text:msgText,iv:'',encryptedKey:'',senderEncryptedKey:'',algorithm:'plain',encrypted:0,recipientKeys:[],senderKeys:[],replyToId:0});
    } else if(activeView==='server'&&activeServer&&activeChannel){
      await connection.invoke('SendGroupMessage',activeServer.groupId,activeChannel.channelId,msgText);
    }
  }catch(e){ showBanner('Uppladdning misslyckades: '+e.message,'warning'); }
});

// ═══════════════════════════════════════════════════════════
//  POLLING
// ═══════════════════════════════════════════════════════════
function startPolling(){ stopPolling(); pollingTimer=setInterval(async()=>{ if(!me) return; if(document.hidden&&Date.now()-lastSyncAt<POLL_MS) return; await refreshConversations().catch(()=>{}); lastSyncAt=Date.now(); },POLL_MS); }
function stopPolling(){ if(pollingTimer){clearInterval(pollingTimer);pollingTimer=null;} }

// ═══════════════════════════════════════════════════════════
//  UI EVENTS
// ═══════════════════════════════════════════════════════════
function updateCharCount(){
  if(EL.msg.value.length>MAX_MSG_LEN) EL.msg.value=EL.msg.value.slice(0,MAX_MSG_LEN);
  EL.charCount.textContent=EL.msg.value.length;
  EL.msg.style.height='auto';
  EL.msg.style.height=Math.min(EL.msg.scrollHeight,140)+'px';
  updateComposerState();
}

EL.msg.addEventListener('input',()=>{ updateCharCount(); if(activePeer&&activeView==='dm') startLocalTyping(activePeer); });
EL.msg.addEventListener('keydown',e=>{ if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();if(!EL.sendBtn.disabled)sendMessage();} });
EL.sendBtn.addEventListener('click',sendMessage);
EL.railDmBtn.addEventListener('click',switchToDm);
EL.railAddBtn.addEventListener('click',showCreateServerModal);
EL.newDmBtn.addEventListener('click',showNewDmModal);
EL.wStartDm?.addEventListener('click',showNewDmModal);
EL.wCreateServer?.addEventListener('click',showCreateServerModal);
EL.reconnectBtn.addEventListener('click',async()=>{ try{await connection.stop();}catch{} await startRealtime(); });
document.getElementById('searchConv')?.addEventListener('input',debounce(()=>{ if(activeView==='dm') renderDmSidebar(); },200));

document.addEventListener('visibilitychange',async()=>{ if(!document.hidden){ await refreshConversations(); } });
window.addEventListener('beforeunload',()=>{ stopPolling(); stopLocalTyping(activePeer); });

// ═══════════════════════════════════════════════════════════
//  BOOT
// ═══════════════════════════════════════════════════════════
async function boot(){
  updateCharCount();
  try{
    me=await api('/api/me');
  }catch(e){
    EL.meName.innerHTML='<a href="/login.html" style="color:var(--ac)">Logga in</a>';
    EL.meStatus.textContent='Inte inloggad';
    setConnState('error','Ej inloggad');
    renderWelcome(); updateComposerState();
    return;
  }
  const verified=!!(me.twoFactorEnabled||me.emailVerified);
  EL.meAvatar.textContent=initials(me.username);
  EL.meName.innerHTML=`@${esc(me.username)}${verified?'<span class="vbadge" style="margin-left:6px">✓</span>':''}`;
  EL.meStatus.textContent='Privata meddelanden aktiva.';

  registerHandlers();
  await startRealtime();
  await Promise.all([refreshConversations(), loadServers()]);
  startPolling();
  renderWelcome(); updateComposerState();

  // Handle ?dm= param
  const dmParam=new URLSearchParams(location.search).get('dm');
  if(dmParam){ history.replaceState({},'','/chat.html'); switchToDm(); await activateDm(dmParam); }
}

boot();
</script>
<script src="/terminal.js"></script>
</body>
</html>"""

with open('/root/RunSpace/NewServer/chat.html', 'w') as f:
    f.write(html)

print(f"Done — {len(html)} chars")
