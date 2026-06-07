(function () {
  'use strict';

  // ── Starfield ──────────────────────────────────────────────
  function initStars() {
    const canvas = document.getElementById('stars');
    if (!canvas) return;
    for (let i = 0; i < 40; i++) {
      const dot = document.createElement('div');
      dot.className = 'rs-star-dot';
      const size = Math.random() * 1.4 + 0.3;
      dot.style.cssText = [
        'width:'  + size + 'px',
        'height:' + size + 'px',
        'left:'   + (Math.random() * 100) + '%',
        'top:'    + (Math.random() * 100) + '%',
        'opacity:'            + (Math.random() * 0.22 + 0.04).toFixed(3),
        'animation-duration:' + (Math.random() * 35 + 25) + 's',
        'animation-delay:-'   + (Math.random() * 35) + 's',
      ].join(';');
      canvas.appendChild(dot);
    }
  }

  // ── Helpers ────────────────────────────────────────────────
  function getUsername() {
    // Try ?u=username first, then /profile/username path
    const params = new URLSearchParams(window.location.search);
    if (params.get('u')) return params.get('u');
    const parts = window.location.pathname.replace(/\/$/, '').split('/');
    const last = parts[parts.length - 1];
    return (last && last !== 'profile') ? last : null;
  }

  function escapeHtml(str) {
    if (!str) return '';
    return str
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function show(id) { document.getElementById(id).classList.remove('hidden'); }
  function hide(id) { document.getElementById(id).classList.add('hidden'); }

  // ── Render profile ─────────────────────────────────────────
  function renderProfile(data, isLoggedIn) {
    hide('profile-loading');
    show('profile-main');

    document.title = data.username + ' — RunSpace';

    // Banner
    const banner = document.getElementById('profile-banner');
    if (data.bannerUrl) {
      banner.style.backgroundImage = 'url(' + data.bannerUrl + ')';
    } else {
      banner.style.background = 'linear-gradient(135deg, #0d0d18 0%, #0f0f2a 100%)';
    }

    // Avatar
    const avatar = document.getElementById('profile-avatar');
    if (data.avatarUrl) {
      avatar.style.backgroundImage = 'url(' + data.avatarUrl + ')';
    } else {
      avatar.style.background = 'linear-gradient(135deg, #1a1a30, #0d0d20)';
      avatar.textContent = data.username.charAt(0).toUpperCase();
      avatar.style.cssText += ';display:flex;align-items:center;justify-content:center;font-size:28px;font-weight:800;color:#3b7dff';
    }

    // Status dot
    const dot = document.getElementById('profile-status-dot');
    const status = (data.status || '').toLowerCase();
    if (status === 'verified' || status === 'admin') dot.classList.add('verified');

    // Username
    document.getElementById('profile-username').textContent = data.username;

    // Premium
    if (data.isPremium) show('profile-premium');

    // Meta
    const meta = document.getElementById('profile-meta');
    const parts = [];
    if (data.age) parts.push(data.age);
    if (data.status) parts.push(data.status.charAt(0).toUpperCase() + data.status.slice(1));
    meta.textContent = parts.join(' · ');

    // Badges
    if (data.badges && data.badges.length) {
      const badgesEl = document.getElementById('profile-badges');
      data.badges.forEach(function (b) {
        const span = document.createElement('span');
        span.className = 'badge';
        span.textContent = typeof b === 'string' ? b : (b.name || b.label || JSON.stringify(b));
        badgesEl.appendChild(span);
      });
      show('profile-badges');
    }

    // Suspended
    if (data.isSuspended || data.isRestricted) {
      show('suspended-notice');
    }

    // Bio
    if (data.bio && data.bio.trim()) {
      document.getElementById('profile-bio').textContent = data.bio;
      show('bio-section');
    }

    // Links
    let links = [];
    try {
      links = typeof data.links === 'string' ? JSON.parse(data.links) : (data.links || []);
    } catch (e) { links = []; }

    if (links.length) {
      const linksEl = document.getElementById('profile-links');
      links.forEach(function (l) {
        const url  = typeof l === 'string' ? l : (l.url || l.href || '');
        const label = typeof l === 'string' ? url : (l.label || l.title || url);
        if (!url) return;
        const a = document.createElement('a');
        a.className = 'link-item';
        a.href = url;
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
        a.innerHTML =
          '<svg viewBox="0 0 24 24"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>' +
          '<path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>' +
          escapeHtml(label);
        linksEl.appendChild(a);
      });
      show('links-section');
    }

    // Stats
    document.getElementById('stat-age').textContent = data.age || '—';
    document.getElementById('stat-status').textContent =
      data.status ? data.status.charAt(0).toUpperCase() + data.status.slice(1) : '—';

    if (data.isPremium && data.premiumPlan) {
      document.getElementById('stat-plan').textContent = data.premiumPlan;
      show('stat-plan-wrap');
    }

    // Message button — only if logged in and not own profile
    if (isLoggedIn && isLoggedIn === data.username) {
      document.getElementById("btn-settings").style.display = "";
      show("profile-actions");
    }
    if (isLoggedIn && isLoggedIn !== data.username) {
      const btn = document.getElementById('btn-message');
      btn.href = '/chatt?dm=' + encodeURIComponent(data.username);
      show('profile-actions');
    }
  }

  // ── Load ───────────────────────────────────────────────────
  function loadProfile(username) {
    // Check if logged in (for extra actions)
    let loggedInAs = null;
    fetch('/api/me')
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (me) {
        loggedInAs = me ? (me.username || null) : null;
      })
      .catch(function () {})
      .finally(function () {
        fetch('/api/profile/public/' + encodeURIComponent(username))
          .then(function (r) {
            if (r.status === 404) throw new Error('notfound');
            if (!r.ok) throw new Error('error');
            return r.json();
          })
          .then(function (data) {
            renderProfile(data, loggedInAs);
          })
          .catch(function (err) {
            hide('profile-loading');
            if (err.message === 'notfound') {
              show('profile-notfound');
            } else {
              show('profile-notfound');
            }
          });
      });
  }

  // ── Init ───────────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', function () {
    initStars();

    const username = getUsername();
    if (!username) {
      fetch('/api/me')
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (me) {
          if (me && me.username) {
            window.location.replace('/profile/' + encodeURIComponent(me.username));
          } else {
            hide('profile-loading');
            show('profile-notfound');
          }
        })
        .catch(function () {
          hide('profile-loading');
          show('profile-notfound');
        });
      return;
    }

    loadProfile(username);
  });
})();
