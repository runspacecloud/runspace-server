(function() {
'use strict';

const COMMANDS = {
  '/chat':    { desc: 'Öppna chatten',         action: () => nav('/chat.html') },
  '/home':    { desc: 'Gå till startsidan',    action: () => nav('/index.html') },
  '/settings':{ desc: 'Öppna inställningar',  action: () => nav('/settings.html') },
  '/profile': { desc: 'Din profil',            action: () => nav('/profile.html') },
  '/account': { desc: 'Kontoinställningar',    action: () => nav('/account.html') },
  '/donate':  { desc: 'Donera',               action: () => nav('/donate.html') },
  '/admin':   { desc: 'Admin-portal',          action: () => nav('/admin_portal.html') },
  '/projekt': { desc: 'Projekt',               action: () => nav('/projekt.html') },
  '/logout':  { desc: 'Logga ut',             action: logout },
  '/clear':   { desc: 'Rensa terminalen',      action: clearOutput },
  '/help':    { desc: 'Visa alla kommandon',   action: showHelp },
  '/private dm': { desc: '/private dm {användare} — öppna DM', action: null },
};

function nav(path) {
  print(`→ Navigerar till ${path}...`, 'success');
  setTimeout(() => window.location.href = path, 400);
}

async function logout() {
  print('→ Loggar ut...', 'success');
  try {
    await fetch('/api/auth/logout', { method: 'POST', credentials: 'include' });
  } catch {}
  setTimeout(() => window.location.href = '/login.html', 500);
}

function showHelp() {
  print('Tillgängliga kommandon:', 'info');
  Object.entries(COMMANDS).forEach(([cmd, { desc }]) => {
    print(`  ${cmd.padEnd(16)} — ${desc}`, 'muted');
  });
}

function clearOutput() {
  const out = document.getElementById('rs-term-output');
  if (out) out.innerHTML = '';
}

function print(text, type = 'normal') {
  const out = document.getElementById('rs-term-output');
  if (!out) return;
  const line = document.createElement('div');
  line.className = 'rs-term-line rs-term-' + type;
  line.textContent = text;
  out.appendChild(line);
  out.scrollTop = out.scrollHeight;
}

// ── Build DOM ──
function buildTerminal() {
  if (document.getElementById('rs-terminal')) return;

  const style = document.createElement('style');
  style.textContent = `
    #rs-terminal {
      display: none;
      position: fixed;
      bottom: 0; left: 0; right: 0;
      height: 280px;
      background: #07080d;
      border-top: 1px solid rgba(79,110,247,.4);
      z-index: 99999;
      flex-direction: column;
      font-family: 'IBM Plex Mono', 'Courier New', monospace;
      animation: rs-term-slide .18s ease;
    }
    #rs-terminal.visible { display: flex; }
    @keyframes rs-term-slide {
      from { transform: translateY(100%); opacity: 0; }
      to   { transform: translateY(0);   opacity: 1; }
    }
    #rs-term-topbar {
      display: flex; align-items: center; justify-content: space-between;
      padding: 6px 14px;
      background: #0c0d18;
      border-bottom: 1px solid rgba(255,255,255,.06);
      flex-shrink: 0;
    }
    #rs-term-title {
      font-size: 11px; color: #4f6ef7; letter-spacing: .5px;
      display: flex; align-items: center; gap: 8px;
    }
    #rs-term-title span { color: #3e4060; }
    #rs-term-close {
      font-size: 11px; color: #3e4060; cursor: pointer;
      padding: 2px 6px; border-radius: 4px;
      transition: color .15s, background .15s;
    }
    #rs-term-close:hover { color: #e04444; background: rgba(224,68,68,.1); }
    #rs-term-output {
      flex: 1; overflow-y: auto; padding: 10px 16px;
      display: flex; flex-direction: column; gap: 3px;
    }
    #rs-term-output::-webkit-scrollbar { width: 3px; }
    #rs-term-output::-webkit-scrollbar-thumb { background: #1d1e2c; border-radius: 2px; }
    .rs-term-line { font-size: 12px; line-height: 1.5; }
    .rs-term-normal { color: #d8daf0; }
    .rs-term-success { color: #1fd882; }
    .rs-term-error   { color: #e04444; }
    .rs-term-info    { color: #4f6ef7; }
    .rs-term-muted   { color: #3e4060; }
    .rs-term-warn    { color: #f0b429; }
    #rs-term-inputrow {
      display: flex; align-items: center; gap: 8px;
      padding: 8px 14px;
      border-top: 1px solid rgba(255,255,255,.06);
      flex-shrink: 0;
    }
    #rs-term-prompt { color: #4f6ef7; font-size: 13px; user-select: none; }
    #rs-term-input {
      flex: 1; background: transparent; border: none;
      color: #d8daf0; font-size: 13px; outline: none;
      font-family: inherit;
    }
    #rs-term-input::placeholder { color: #3e4060; }
    #rs-term-autocomplete {
      font-size: 11px; color: #3e4060;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
      max-width: 300px;
    }
  `;
  document.head.appendChild(style);

  const term = document.createElement('div');
  term.id = 'rs-terminal';
  term.innerHTML = `
    <div id="rs-term-topbar">
      <div id="rs-term-title">
        ❯_ RunSpace Terminal
        <span>Ctrl+Alt+T för att stänga</span>
      </div>
      <div id="rs-term-close">✕ stäng</div>
    </div>
    <div id="rs-term-output"></div>
    <div id="rs-term-inputrow">
      <span id="rs-term-prompt">❯</span>
      <input id="rs-term-input" type="text" placeholder="Skriv ett kommando, t.ex. /help" autocomplete="off" spellcheck="false"/>
      <span id="rs-term-autocomplete"></span>
    </div>
  `;
  document.body.appendChild(term);

  document.getElementById('rs-term-close').addEventListener('click', closeTerminal);

  const input = document.getElementById('rs-term-input');
  const autocomplete = document.getElementById('rs-term-autocomplete');
  const history = [];
  let histIdx = -1;

  input.addEventListener('input', () => {
    const val = input.value;
    const match = Object.keys(COMMANDS).find(cmd => cmd.startsWith(val) && val.length > 0 && cmd !== val);
    autocomplete.textContent = match ? match.slice(val.length) : '';
  });

  input.addEventListener('keydown', async e => {
    if (e.key === 'Escape') { closeTerminal(); return; }

    if (e.key === 'Tab') {
      e.preventDefault();
      const val = input.value;
      const match = Object.keys(COMMANDS).find(cmd => cmd.startsWith(val) && val.length > 0);
      if (match) { input.value = match + ' '; autocomplete.textContent = ''; }
      return;
    }

    if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (history.length) { histIdx = Math.min(histIdx + 1, history.length - 1); input.value = history[histIdx]; }
      return;
    }

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      histIdx = Math.max(histIdx - 1, -1);
      input.value = histIdx >= 0 ? history[histIdx] : '';
      return;
    }

    if (e.key === 'Enter') {
      const raw = input.value.trim();
      input.value = '';
      autocomplete.textContent = '';
      if (!raw) return;
      history.unshift(raw);
      histIdx = -1;
      print('❯ ' + raw, 'muted');
      await executeCommand(raw);
    }
  });
}

async function executeCommand(raw) {
  const lower = raw.toLowerCase();

  // Check exact match first
  if (COMMANDS[lower] && COMMANDS[lower].action) {
    COMMANDS[lower].action();
    return;
  }

  // /private dm {user}
  if (lower.startsWith('/private dm ')) {
    const username = raw.slice(12).trim().toLowerCase();
    if (!username) { print('Ange ett användarnamn: /private dm {användare}', 'error'); return; }
    print(`→ Öppnar DM med @${username}...`, 'success');
    setTimeout(() => window.location.href = `/chat.html?dm=${encodeURIComponent(username)}`, 400);
    return;
  }

  // /profile {user} — publik profil
  if (lower.startsWith('/profile ')) {
    const username = raw.slice(9).trim().toLowerCase();
    if (!username) { print('Ange ett användarnamn: /profile {användare}', 'error'); return; }
    print(`→ Öppnar profil för @${username}...`, 'success');
    setTimeout(() => window.location.href = `/public_profile.html?u=${encodeURIComponent(username)}`, 400);
    return;
  }

  print(`Okänt kommando: ${raw.split(' ')[0]}`, 'error');
  print('Skriv /help för att se alla kommandon.', 'muted');
}

function openTerminal() {
  const term = document.getElementById('rs-terminal');
  if (!term) return;
  term.classList.add('visible');
  setTimeout(() => document.getElementById('rs-term-input')?.focus(), 50);
  print('RunSpace Terminal — skriv /help för kommandon.', 'info');
}

function closeTerminal() {
  const term = document.getElementById('rs-terminal');
  if (term) term.classList.remove('visible');
}

function toggleTerminal() {
  const term = document.getElementById('rs-terminal');
  if (!term) return;
  if (term.classList.contains('visible')) closeTerminal();
  else openTerminal();
}

// ── Keyboard shortcut ──
document.addEventListener('keydown', e => {
  if (e.ctrlKey && e.altKey && e.key === 't') {
    e.preventDefault();
    toggleTerminal();
  }
});

// ── Init ──
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', buildTerminal);
} else {
  buildTerminal();
}

// ── Handle ?dm= param on chat.html ──
if (window.location.pathname.endsWith('chat.html')) {
  const dmUser = new URLSearchParams(window.location.search).get('dm');
  if (dmUser) {
    window.addEventListener('load', () => {
      // Remove param from URL cleanly
      history.replaceState({}, '', '/chat.html');
      // Wait for chat runtime then activate DM
      const tryActivate = setInterval(() => {
        if (typeof activateConversation === 'function') {
          clearInterval(tryActivate);
          activateConversation(dmUser);
        }
      }, 200);
    });
  }
}

})();
