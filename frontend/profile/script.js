
function badgeIcon(name) {
  const b = (name || '').toLowerCase();

  const icons = {
    verified: '<svg viewBox="0 0 24 24"><path d="M9.2 16.6 4.9 12.3l1.7-1.7 2.6 2.6 8.2-8.2 1.7 1.7z"/></svg>',
    owner: '<svg viewBox="0 0 24 24"><path d="M12 2 4 5.5v6.2c0 5 3.4 9.6 8 10.8 4.6-1.2 8-5.8 8-10.8V5.5z"/></svg>',
    founder: '<svg viewBox="0 0 24 24"><path d="M5 16 3 6l5 4 4-7 4 7 5-4-2 10zM5 19h14v2H5z"/></svg>',
    developer: '<svg viewBox="0 0 24 24"><path d="M8.7 16.3 4.4 12l4.3-4.3L7.3 6.3 1.6 12l5.7 5.7zm6.6 0L19.6 12l-4.3-4.3 1.4-1.4 5.7 5.7-5.7 5.7zM10.2 20l2.1-16h2L12.2 20z"/></svg>',
    moderator: '<svg viewBox="0 0 24 24"><path d="M12 2 3 6v6c0 5 3.8 9.4 9 10 5.2-.6 9-5 9-10V6zm-1 14-4-4 1.4-1.4 2.6 2.6 5.6-5.6L18 9z"/></svg>',
    architect: '<svg viewBox="0 0 24 24"><path d="M3 21h18v-2H3zm2-4h14L12 3zm4-2 3-6 3 6z"/></svg>',
    visionary: '<svg viewBox="0 0 24 24"><path d="M12 4C7 4 3.1 7.1 1.5 12 3.1 16.9 7 20 12 20s8.9-3.1 10.5-8C20.9 7.1 17 4 12 4zm0 13a5 5 0 1 1 0-10 5 5 0 0 1 0 10z"/></svg>',
    partner: '<svg viewBox="0 0 24 24"><path d="M8 12a4 4 0 1 1 0-8 4 4 0 0 1 0 8zm8 0a4 4 0 1 1 0-8 4 4 0 0 1 0 8zM2 20c.5-3.3 3-6 6-6s5.5 2.7 6 6zm8 0c.4-2.2 1.5-4.1 3.2-5.2.8-.5 1.7-.8 2.8-.8 3 0 5.5 2.7 6 6z"/></svg>',
    tester: '<svg viewBox="0 0 24 24"><path d="M9 2h6v2l-1 1v4.6l5.7 8.6A2.5 2.5 0 0 1 17.6 22H6.4a2.5 2.5 0 0 1-2.1-3.8L10 9.6V5L9 4z"/></svg>',
    contributor: '<svg viewBox="0 0 24 24"><path d="M12 2 2 7l10 5 10-5zm-7 8v7l7 3.5 7-3.5v-7l-7 3.5z"/></svg>',
    "bug-hunter": '<svg viewBox="0 0 24 24"><path d="M20 8h-3.2c-.4-.7-.9-1.3-1.6-1.8L17 4.4 15.6 3l-2.3 2.3a7 7 0 0 0-2.6 0L8.4 3 7 4.4l1.8 1.8C8.1 6.7 7.6 7.3 7.2 8H4v2h2.4c-.1.3-.1.7-.1 1v1H4v2h2.3v1c0 .3 0 .7.1 1H4v2h3.2a6 6 0 0 0 9.6 0H20v-2h-2.4c.1-.3.1-.7.1-1v-1H20v-2h-2.3v-1c0-.3 0-.7-.1-1H20z"/></svg>',
    "early-supporter": '<svg viewBox="0 0 24 24"><path d="M12 21.4 10.6 20C5.4 15.3 2 12.2 2 8.4 2 5.3 4.4 3 7.5 3c1.7 0 3.4.8 4.5 2.1C13.1 3.8 14.8 3 16.5 3 19.6 3 22 5.3 22 8.4c0 3.8-3.4 6.9-8.6 11.6z"/></svg>'
  };

  return icons[b] || '<svg viewBox="0 0 24 24"><path d="M12 2 15 8l6 .9-4.5 4.3 1.1 6.1L12 16.5 6.4 19.3l1.1-6.1L3 8.9 9 8z"/></svg>';
}

function prettyBadge(name) {
  return (name || '')
    .replace(/-/g, ' ')
    .replace(/\b\w/g, c => c.toUpperCase());
}

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
    const usernameEl = document.getElementById('profile-username');
    usernameEl.textContent = data.username;
    if (status === 'verified' || status === 'admin') {
      const check = document.createElement('span');
      check.className = 'verified-check';
      check.title = 'Verified';
      check.textContent = '✓';
      usernameEl.appendChild(check);
    }

    // Premium
    if (data.isPremium) show('profile-premium');

    // Meta
    const meta = document.getElementById('profile-meta');
    const parts = [];
    if (data.age) parts.push(data.age);
    if (data.status && status !== 'verified' && status !== 'admin') parts.push(data.status.charAt(0).toUpperCase() + data.status.slice(1));
    meta.textContent = parts.join(' · ');

    // Badges
    if (data.badges && data.badges.length) {
      const badgesEl = document.getElementById('profile-badges');
      badgesEl.innerHTML = '';

      const teamRoles = ["owner", "founder", "developer", "moderator", "support"];
      let hasTeamRole = false;
      let teamCount = 0;
      let communityCount = 0;

      const cleanBadges = data.badges
        .map(b => String(b || '').trim())
        .filter(Boolean)
        .filter(b => b.toLowerCase() !== "verified");

      const MAX_VISIBLE_BADGES = 5;
      let visibleBadges = 0;
      let hiddenBadges = 0;

      cleanBadges.forEach(function (b) {
        const key = b.toLowerCase();
        const isTeamRole = teamRoles.includes(key);

        if (isTeamRole) {
          hasTeamRole = true;
        }

        if (visibleBadges >= MAX_VISIBLE_BADGES) {
          hiddenBadges++;
          return;
        }

        visibleBadges++;

        const span = document.createElement("span");
        span.className = "badge role-badge role-" + key.replace(/[^a-z0-9-]/g, "");
        span.innerHTML = badgeIcon(b) + "<span>" + prettyBadge(b) + "</span>";
        badgesEl.appendChild(span);
      });

      if (hiddenBadges > 0) {
        const more = document.createElement("button");
        more.type = "button";
        more.className = "badge role-badge more-badges";
        more.textContent = "+" + hiddenBadges + " more badges";

        more.addEventListener("click", function () {
          badgesEl.innerHTML = "";

          cleanBadges.forEach(function (b) {
            const key = b.toLowerCase();
            const span = document.createElement("span");
            span.className = "badge role-badge role-" + key.replace(/[^a-z0-9-]/g, "");
            span.innerHTML = badgeIcon(b) + "<span>" + prettyBadge(b) + "</span>";
            badgesEl.appendChild(span);
          });

          if (hasTeamRole) {
            const team = document.createElement("span");
            team.className = "badge role-badge team-badge";
            team.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 2 4 5.5v6.2c0 5 3.4 9.6 8 10.8 4.6-1.2 8-5.8 8-10.8V5.5zM11 15.5 7.8 12.3l1.4-1.4 1.8 1.8 4.8-4.8 1.4 1.4z"/></svg><span>RunSpace Team</span>';
            badgesEl.prepend(team);
          }
        });

        badgesEl.appendChild(more);
      }

      if (hasTeamRole) {
        const team = document.createElement("span");
        team.className = "badge role-badge team-badge";
        team.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 2 4 5.5v6.2c0 5 3.4 9.6 8 10.8 4.6-1.2 8-5.8 8-10.8V5.5zM11 15.5 7.8 12.3l1.4-1.4 1.8 1.8 4.8-4.8 1.4 1.4z"/></svg><span>RunSpace Team</span>';
        badgesEl.prepend(team);
      }

      if (badgesEl.children.length) show('profile-badges');
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
    const statStatus = document.getElementById('stat-status');
    if (statStatus) {
      const statBox = statStatus.closest('.stat-card') || statStatus.parentElement;
      if (status === 'verified' || status === 'admin') {
        if (statBox) statBox.style.display = 'none';
      } else {
        statStatus.textContent =
          data.status ? data.status.charAt(0).toUpperCase() + data.status.slice(1) : '—';
      }
    }

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
