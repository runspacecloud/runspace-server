// === RunSpace Chat Polish v1 — Discord-feel overhaul ===
// Add <script src="/chat-polish.js"></script> before </body> in chatt.html
(function(){

// ═══════════════════════════════════════
// 1. INJECT CSS OVERHAUL
// ═══════════════════════════════════════
var css = document.createElement('style');
css.id = 'rsPolish';
css.textContent = `
/* ── Global transitions ── */
*{transition-property:background,border-color,color,opacity,transform,box-shadow;transition-duration:.15s;transition-timing-function:ease}
textarea,input,.messages,.messages *{transition:none}

/* ── Tighter spacing (more app-like) ── */
.topbar{height:44px;padding:0 12px}
.sidebar{background:#0c1220}
.sidebar-head{padding:10px 12px}
.sidebar-scroll{padding:4px 6px;gap:1px}

/* ── Conversation list — alive & clickable ── */
.conv-item{padding:8px 10px;border-radius:8px;border-left:none;margin:1px 0;transition:background .12s,transform .1s}
.conv-item:hover{background:rgba(59,130,246,.06);transform:translateX(2px)}
.conv-item.active{background:rgba(59,130,246,.12);border-left:none;box-shadow:inset 3px 0 0 #3b82f6}
.conv-item.active:hover{background:rgba(59,130,246,.16)}
.conv-avatar{transition:transform .15s}
.conv-item:hover .conv-avatar{transform:scale(1.05)}
.conv-name{font-size:13px;font-weight:700}
.conv-preview{font-size:11px;color:#4b5563;margin-top:2px;max-width:160px}
.conv-item.active .conv-preview{color:#64748b}

/* ── Chat area — less empty ── */
.chat{background:#0a0f1a}
.messages{padding:12px 14px 8px;gap:1px}

/* ── Message animations ── */
@keyframes msgIn{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:translateY(0)}}
.msg-row{animation:msgIn .2s ease;padding:2px 0}
.msg-row+.msg-row{margin-top:1px}
.msg-row.grouped{margin-top:0}

/* ── Message bubbles — tighter ── */
.msg-bubble{padding:7px 11px;font-size:13px;line-height:1.45;border-radius:10px;max-width:min(72%,640px)}
.msg-row.mine .msg-bubble{border-bottom-right-radius:3px;background:#2563eb}
.msg-row.other .msg-bubble{border-bottom-left-radius:3px;background:#161f30;border:1px solid #1e293b}
.msg-row.mine .msg-bubble:hover{background:#1d4ed8}
.msg-row.other .msg-bubble:hover{background:#1a2538}

/* ── Message meta — subtle ── */
.msg-meta{font-size:10px;color:#475569;padding:0 2px;margin-bottom:1px}

/* ── Hover actions toolbar ── */
.msg-toolbar{background:#111827;border:1px solid #1e293b;border-radius:7px;box-shadow:0 4px 16px rgba(0,0,0,.4);padding:2px;gap:1px}
.msg-toolbar-btn{width:26px;height:26px;font-size:13px;border-radius:5px}
.msg-toolbar-btn:hover{background:rgba(255,255,255,.08);transform:scale(1.1)}

/* ── Composer — app-like ── */
.composer{padding:6px 12px 8px;border-top:1px solid #111827;background:#0c1220}
.composer-inner{background:#111827;border:1px solid #1e293b;border-radius:12px;padding:4px 8px;min-height:42px}
.composer-inner:focus-within{border-color:#2563eb;box-shadow:0 0 0 2px rgba(37,99,235,.15)}
.textarea{font-size:13.5px;padding:8px 6px;min-height:24px}
.textarea::placeholder{color:#4b5563}
.btn-send{width:32px;height:32px;border-radius:8px;font-size:15px;transition:background .12s,transform .1s}
.btn-send:hover:not(:disabled){transform:scale(1.08)}
.btn-send:active:not(:disabled){transform:scale(.95)}
.composer-file-btn{transition:color .12s,transform .1s}
.composer-file-btn:hover{transform:scale(1.1);color:#e2e8f0}

/* ── Chat header — functional ── */
.chat-head{min-height:44px;padding:0 14px;background:#0c1220;border-bottom:1px solid #111827;box-shadow:none}
.chat-head-name{font-size:14px;font-weight:800}
.chat-head-btn{width:30px;height:30px;border-radius:7px;font-size:15px;transition:background .12s,transform .1s}
.chat-head-btn:hover{background:rgba(255,255,255,.08);transform:scale(1.08)}

/* ── Topbar — compact ── */
.topbar{background:#080c16;border-bottom:1px solid #0f1627}
.brand-title{font-size:14px}
.status-pill{font-size:10px;padding:4px 8px}

/* ── User panel — tight ── */
.user-panel{padding:6px 8px;background:#080c16;border-top:1px solid #111827}
.up-btn{transition:background .12s,transform .1s}
.up-btn:hover{transform:scale(1.08)}

/* ── Scrollbar — Discord-style ── */
.messages::-webkit-scrollbar,.sidebar-scroll::-webkit-scrollbar,.conv-list::-webkit-scrollbar{width:6px}
.messages::-webkit-scrollbar-track,.sidebar-scroll::-webkit-scrollbar-track,.conv-list::-webkit-scrollbar-track{background:transparent}
.messages::-webkit-scrollbar-thumb,.sidebar-scroll::-webkit-scrollbar-thumb,.conv-list::-webkit-scrollbar-thumb{background:#1e293b;border-radius:3px}
.messages::-webkit-scrollbar-thumb:hover,.sidebar-scroll::-webkit-scrollbar-thumb:hover{background:#334155}

/* ── Reaction chips — tighter ── */
.reaction-chip{padding:2px 5px;border-radius:10px;font-size:11px;transition:background .1s,transform .1s}
.reaction-chip:hover{transform:scale(1.05)}

/* ── Reply bar ── */
.reply-bar{border-radius:8px;padding:5px 10px;font-size:11px;animation:msgIn .15s ease}

/* ── Typing indicator ── */
.typing-indicator{padding:2px 14px;font-size:10px;height:18px}

/* ── Server list — compact ── */
.server-list{background:#060910;padding:8px 0;gap:4px}
.srv{width:38px;height:38px;font-size:14px;transition:border-radius .15s,border-color .15s,transform .15s}
.srv:hover{transform:scale(1.06)}

/* ── Modal polish ── */
.modal{box-shadow:0 24px 60px rgba(0,0,0,.6)}

/* ── Empty state ── */
.empty-state{padding:32px 24px;max-width:360px}
.empty-state h2{font-size:13px;color:#475569;font-weight:600}
.empty-state p{font-size:12px;color:#374151}

/* ── Attachment preview ── */
.attachment-preview{border-radius:10px;animation:msgIn .15s ease}

/* ── Online dot pulse ── */
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.5}}
.conv-avatar.online .online-dot{animation:pulse 2s ease infinite}
`;
document.head.appendChild(css);

// ═══════════════════════════════════════
// 2. BETTER EMPTY STATE
// ═══════════════════════════════════════
function betterEmptyState(){
  var msgs = document.getElementById('messages');
  if(!msgs) return;
  var observer = new MutationObserver(function(){
    var empty = msgs.querySelector('.empty-state');
    if(empty && empty.dataset.polished !== '1'){
      empty.dataset.polished = '1';
      var peer = typeof currentPeer !== 'undefined' ? currentPeer : '';
      if(peer && peer !== ''){
        empty.innerHTML = '<div style="text-align:center;padding:40px 20px">'
          +'<div style="width:56px;height:56px;border-radius:50%;background:#161f30;margin:0 auto 12px;display:flex;align-items:center;justify-content:center;font-size:22px;font-weight:800;color:#3b82f6">'+(peer[0]||'?').toUpperCase()+'</div>'
          +'<div style="font-size:14px;font-weight:700;color:#e2e8f0;margin-bottom:4px">@'+peer+'</div>'
          +'<div style="font-size:12px;color:#475569;margin-bottom:16px">Det här är början på er konversation.</div>'
          +'<div style="font-size:11px;color:#374151">Skriv ett meddelande för att börja chatta</div>'
          +'</div>';
      }
    }
  });
  observer.observe(msgs, {childList:true, subtree:true});
}

// ═══════════════════════════════════════
// 3. DYNAMIC PLACEHOLDER
// ═══════════════════════════════════════
function dynamicPlaceholder(){
  var textarea = document.getElementById('msg');
  if(!textarea) return;
  var observer = new MutationObserver(function(){
    var peer = typeof currentPeer !== 'undefined' ? currentPeer : '';
    if(peer) textarea.placeholder = 'Skriv till @' + peer + '...';
    else textarea.placeholder = 'Skriv ett meddelande...';
  });
  // Watch chat title changes
  var title = document.getElementById('chatTitle');
  if(title) observer.observe(title, {childList:true, characterData:true, subtree:true});
  // Also set on click
  document.addEventListener('click', function(e){
    if(e.target.closest('.conv-item')){
      setTimeout(function(){
        var peer = typeof currentPeer !== 'undefined' ? currentPeer : '';
        if(peer) textarea.placeholder = 'Skriv till @' + peer + '...';
      }, 50);
    }
  });
}

// ═══════════════════════════════════════
// 4. ONLINE STATUS IN CHAT HEADER
// ═══════════════════════════════════════
function headerStatus(){
  var chatHead = document.querySelector('.chat-head');
  if(!chatHead || chatHead.querySelector('#headerOnline')) return;
  var dot = document.createElement('span');
  dot.id = 'headerOnline';
  dot.style.cssText = 'width:8px;height:8px;border-radius:50%;background:#475569;flex-shrink:0;margin-left:4px;transition:background .3s';
  var nameEl = document.getElementById('chatTitle');
  if(nameEl) nameEl.parentNode.insertBefore(dot, nameEl.nextSibling);
  // Update periodically
  setInterval(function(){
    var peer = typeof currentPeer !== 'undefined' ? currentPeer : '';
    if(peer && typeof isOnline === 'function'){
      dot.style.background = isOnline(peer) ? '#22c55e' : '#475569';
      dot.title = isOnline(peer) ? 'Online' : 'Offline';
    }
  }, 2000);
}

// ═══════════════════════════════════════
// 5. CONVERSATION TIMESTAMPS
// ═══════════════════════════════════════
function addConvTimestamps(){
  // Patch renderConvList to add timestamps
  if(typeof window.renderConvList !== 'function') return;
  var _orig = window.renderConvList;
  window.renderConvList = function(){
    _orig.apply(this, arguments);
    // Add timestamps to each conv-item
    document.querySelectorAll('.conv-item').forEach(function(item){
      if(item.querySelector('.conv-time')) return;
      var peer = item.dataset.peer;
      if(!peer || typeof convos === 'undefined') return;
      var msgs = convos.get(peer);
      if(!msgs || !msgs.length) return;
      var last = msgs[msgs.length - 1];
      if(!last || !last.ts) return;
      var d = new Date(last.ts);
      var now = new Date();
      var diff = now - d;
      var timeStr = '';
      if(diff < 60000) timeStr = 'nu';
      else if(diff < 3600000) timeStr = Math.floor(diff/60000) + 'm';
      else if(diff < 86400000) timeStr = Math.floor(diff/3600000) + 'h';
      else timeStr = Math.floor(diff/86400000) + 'd';
      var info = item.querySelector('.conv-info');
      if(info){
        var ts = document.createElement('span');
        ts.className = 'conv-time';
        ts.style.cssText = 'font-size:10px;color:#475569;flex-shrink:0;margin-left:auto';
        ts.textContent = timeStr;
        item.style.display = 'flex';
        item.appendChild(ts);
      }
    });
  };
}

// ═══════════════════════════════════════
// INIT
// ═══════════════════════════════════════
function init(){
  betterEmptyState();
  dynamicPlaceholder();
  headerStatus();
  addConvTimestamps();
}

if(document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function(){ setTimeout(init, 200); });
else setTimeout(init, 200);

})();
