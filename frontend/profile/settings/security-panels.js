(function () {
  'use strict';

  function esc(v) {
    return String(v == null ? '' : v).replace(/[&<>"']/g, function (c) {
      return ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'})[c];
    });
  }

  function fmtTime(v) {
    if (!v) return 'Unknown time';
    var d = new Date(v);
    if (isNaN(d.getTime())) return esc(v);
    return d.toLocaleString([], { year:'numeric', month:'short', day:'2-digit', hour:'2-digit', minute:'2-digit' });
  }

  function prettyAction(a) {
    var map = {
      login_with_key: 'Signed in with Account Key',
      register_with_key: 'Account created with Account Key',
      rotate_account_key: 'Account Key changed',
      account_recovery_redeemed: 'Account recovered',
      account_recovery_codes_generated: 'Recovery codes generated',
      '2fa_enabled': '2FA enabled',
      '2fa_disabled': '2FA disabled',
      logout: 'Signed out',
      logout_all: 'Signed out everywhere',
      email_verified: 'Email verified'
    };
    return map[a] || String(a || 'Security event').replace(/_/g, ' ');
  }

  function prettyDevice(ua) {
    ua = String(ua || '');
    var browser = /Firefox/i.test(ua) ? 'Firefox'
      : /Chrome|Chromium/i.test(ua) ? 'Chromium/Chrome'
      : /Safari/i.test(ua) ? 'Safari'
      : 'Browser';

    var os = /Linux/i.test(ua) ? 'Linux'
      : /Windows/i.test(ua) ? 'Windows'
      : /Android/i.test(ua) ? 'Android'
      : /iPhone|iPad/i.test(ua) ? 'iOS'
      : /Mac OS/i.test(ua) ? 'macOS'
      : 'Unknown OS';

    return browser + ' on ' + os;
  }

  function maskIp(d) {
    return String(d || '').replace(/\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b/g, '$1.$2.$3.xxx');
  }

  function prettyDetails(d) {
    d = String(d || '').replace(/^From\s+/i, 'IP ');
    return maskIp(d);
  }

  async function getJson(url) {
    var r = await fetch(url, { credentials: 'include' });
    if (!r.ok) throw new Error(url + ' -> ' + r.status);
    return await r.json();
  }

  async function loadSessions() {
    var el = document.getElementById('session-list');
    if (!el) return;

    try {
      var data;
      try {
        data = await getJson('/api/auth/active-sessions');
      } catch (_) {
        data = await getJson('/api/auth/sessions');
      }

      var sessions = Array.isArray(data) ? data : (data.sessions || data.activeSessions || []);
      if (!sessions.length) {
        el.innerHTML = '<div class="log-empty">No active sessions found. Log out and sign in once to create a fresh session record.</div>';
        return;
      }

      el.innerHTML = sessions.map(function (s) {
        var rawUa = s.userAgent || s.UserAgent || s.deviceName || s.DeviceName || 'Unknown device';
        var ua = prettyDevice(rawUa);
        var ip = maskIp(s.ip || s.Ip || s.ipAddress || s.IpAddress || 'Unknown IP');
        var last = s.lastActivity || s.LastActivity || s.createdAt || s.CreatedAt;
        return '' +
          '<div class="log-item">' +
            '<div class="log-info">' +
              '<div class="log-title">' + esc(ua) + '</div>' +
              '<div class="log-meta">' + esc(ip) + ' · Last active ' + fmtTime(last) + '</div>' +
            '</div>' +
          '</div>';
      }).join('');
    } catch (err) {
      el.innerHTML = '<div class="log-empty">Could not load sessions.</div>';
      console.warn('[settings] sessions failed', err);
    }
  }

  async function loadLoginHistory() {
    var el = document.getElementById('login-history-list');
    if (!el) return;

    try {
      var data = await getJson('/api/auth/login-history');
      var items = Array.isArray(data) ? data : (data.items || data.history || data.logs || []);

      if (!items.length) {
        el.innerHTML = '<div class="log-empty">No login history yet.</div>';
        return;
      }

      el.innerHTML = items.slice(0, 20).map(function (x) {
        var rawAction = x.action || x.Action || x.event || x.Event || 'security_event';
        var action = prettyAction(rawAction);
        var details = prettyDetails(x.details || x.Details || x.deviceName || x.DeviceName || '');
        var time = x.timestamp || x.Timestamp || x.createdAt || x.CreatedAt || x.time || x.Time;
        return '' +
          '<div class="log-item">' +
            '<div class="log-info">' +
              '<div class="log-title">' + esc(action) + '</div>' +
              '<div class="log-meta">' + esc(details) + (details ? ' · ' : '') + fmtTime(time) + '</div>' +
            '</div>' +
          '</div>';
      }).join('');
    } catch (err) {
      el.innerHTML = '<div class="log-empty">Could not load login history.</div>';
      console.warn('[settings] login history failed', err);
    }
  }

  function clarifyDevicesText() {
    var el = document.getElementById('device-list');
    var btn = document.getElementById('btn-revoke-all-devices');
    var actionCard = btn ? btn.closest('.card') : null;
    if (!el) return;

    if (/No devices found/i.test(el.textContent || '') || /Loading/i.test(el.textContent || '') || /No encrypted device keys/i.test(el.textContent || '')) {
      el.innerHTML = '<div class="log-empty">No encryption device keys found yet. Login sessions are shown under Sessions.</div>';
      if (btn) btn.style.display = 'none';
      if (actionCard) actionCard.style.display = 'none';
    } else {
      if (btn) btn.style.display = 'inline-flex';
      if (actionCard) actionCard.style.display = '';
    }
  }

  function boot() {
    loadSessions();
    loadLoginHistory();
    setTimeout(clarifyDevicesText, 800);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
