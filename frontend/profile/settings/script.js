(function () {
  'use strict';

  // ── Starfield ──────────────────────────────────────────────
  function initStars() {
    const canvas = document.getElementById('stars');
    if (!canvas) return;
    for (let i = 0; i < 150; i++) {
      const dot = document.createElement('div');
      dot.className = 'rs-star-dot';
      const size = Math.random() * 1.4 + 0.3;
      dot.style.cssText = [
        'width:'  + size + 'px',
        'height:' + size + 'px',
        'left:'   + (Math.random() * 100) + '%',
        'top:0',
        'opacity:'            + (Math.random() * 0.22 + 0.04).toFixed(3),
        'animation-duration:' + (Math.random() * 35 + 25) + 's',
        'animation-delay:-'   + (Math.random() * 35) + 's',
      ].join(';');
      canvas.appendChild(dot);
    }
  }

  // ── Sidebar navigation ─────────────────────────────────────
  function initNav() {
    const items = document.querySelectorAll('.sidebar-item');
    items.forEach(function (item) {
      item.addEventListener('click', function (e) {
        e.preventDefault();
        const target = item.dataset.section;
        items.forEach(function (i) { i.classList.remove('active'); });
        item.classList.add('active');
        document.querySelectorAll('.settings-section').forEach(function (s) {
          s.classList.remove('active');
        });
        const sec = document.getElementById('section-' + target);
        if (sec) sec.classList.add('active');
        // Update hash for bookmarking
        history.replaceState(null, '', '#' + target);
      });
    });
    // Restore from hash
    const hash = window.location.hash.replace('#', '');
    if (hash) {
      const match = document.querySelector('[data-section="' + hash + '"]');
      if (match) match.click();
    }
  }

  // ── Load /api/me and populate ──────────────────────────────
  function loadMe() {
    fetch('/api/me')
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (me) {
        if (!me) {
          window.location.href = '/login/';
          return;
        }
        populateAll(me);
      })
      .catch(function () {
        window.location.href = '/login/';
      });
  }

  function populateAll(me) {
    // Topbar
    var topbarUser = document.getElementById('topbar-user');
    if (topbarUser) topbarUser.textContent = me.username;

    // Avatar
    var avatarEl = document.getElementById('avatar-preview');
    if (avatarEl) {
      if (me.avatarUrl) {
        avatarEl.style.backgroundImage = 'url(' + me.avatarUrl + ')';
      } else {
        avatarEl.textContent = (me.username || '?').charAt(0).toUpperCase();
      }
    }

    // Preview
    var prevName = document.getElementById('preview-username');
    if (prevName) prevName.textContent = me.username || '—';
    var prevMeta = document.getElementById('preview-meta');
    if (prevMeta) {
      var parts = [];
      if (me.status) parts.push(me.status.charAt(0).toUpperCase() + me.status.slice(1));
      if (me.age) parts.push('Joined ' + me.age + ' ago');
      prevMeta.textContent = parts.join(' · ') || '—';
    }

    // Profile fields
    setVal('field-username', me.username);
    setVal('field-bio', me.bio || '');
    setVal('field-email', me.email || '');

    // Banner
    if (me.bannerUrl) {
      var bp = document.getElementById('banner-preview');
      if (bp) bp.style.backgroundImage = 'url(' + me.bannerUrl + ')';
    }

    // Email verification badges
    if (me.emailVerified) {
      show('email-verified-badge');
    } else {
      show('email-unverified-badge');
    }

    // 2FA
    var tfaText = document.getElementById('tfa-status-text');
    if (tfaText) {
      tfaText.textContent = me.twoFactorEnabled ? '2FA Enabled' : '2FA Disabled';
      tfaText.style.color = me.twoFactorEnabled ? 'var(--success)' : 'var(--text-2)';
    }

    // Trust bars
    if (me.trust && me.trust.dimensions) {
      animateTrustBar('trust-identity', 'trust-identity-val', me.trust.dimensions.identity);
      animateTrustBar('trust-behavior', 'trust-behavior-val', me.trust.dimensions.behavior);
      animateTrustBar('trust-device',   'trust-device-val',   me.trust.dimensions.device);
    }

    // Developer IDs
    setCode('dev-user-id', me.username || '—');
    setCode('dev-public-id', me.publicId || '—');

    // Device list
    populateDevices(me.deviceKeys || []);

    // Links
    var links = [];
    try { links = typeof me.links === 'string' ? JSON.parse(me.links) : (me.links || []); } catch(e){}
    populateLinks(links);
  }

  function animateTrustBar(barId, valId, value) {
    var bar = document.getElementById(barId);
    var val = document.getElementById(valId);
    if (!bar || !val) return;
    var pct = Math.max(0, Math.min(100, value || 0));
    setTimeout(function () { bar.style.width = pct + '%'; }, 100);
    val.textContent = Math.round(pct);
  }

  function populateDevices(devices) {
    var list = document.getElementById('device-list');
    if (!list) return;
    if (!devices.length) {
      list.innerHTML = '<div class="log-empty">No devices found.</div>';
      return;
    }
    list.innerHTML = '';
    var currentDevice = localStorage.getItem('rs_device_id') || '';
    devices.forEach(function (d) {
      var isCurrent = d.deviceId === currentDevice;
      var isDesktop = d.deviceName && d.deviceName.toLowerCase().indexOf('desktop') !== -1;
      var isMobile  = d.deviceName && /android|iphone|mobile/i.test(d.deviceName);

      var svgIcon = isDesktop
        ? '<svg viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>'
        : isMobile
          ? '<svg viewBox="0 0 24 24"><rect x="5" y="2" width="14" height="20" rx="2"/><line x1="12" y1="18" x2="12.01" y2="18"/></svg>'
          : '<svg viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>';

      var date = new Date(d.lastUsedAt);
      var dateStr = date.toLocaleDateString('en-GB', { day:'numeric', month:'short', year:'numeric' });

      var el = document.createElement('div');
      el.className = 'device-item';
      el.innerHTML =
        '<div class="device-icon">' + svgIcon + '</div>' +
        '<div class="log-info">' +
          '<div class="log-title">' + escapeHtml(d.deviceName || d.deviceId) + '</div>' +
          '<div class="log-meta">Last used ' + dateStr + '</div>' +
        '</div>' +
        (isCurrent ? '<span class="device-current">This device</span>' : '') +
        '<button class="btn-revoke" data-device-id="' + escapeHtml(d.deviceId) + '">Revoke</button>';
      list.appendChild(el);
    });

    // Revoke buttons
    list.querySelectorAll('.btn-revoke').forEach(function (btn) {
      btn.addEventListener('click', function () {
        if (!confirm('Revoke this device key? It will need to re-register.')) return;
        fetch('/api/me/devices/' + encodeURIComponent(btn.dataset.deviceId), { method: 'DELETE' })
          .then(function (r) {
            if (r.ok) { btn.closest('.device-item').remove(); }
            else alert('Failed to revoke device.');
          });
      });
    });
  }

  function populateLinks(links) {
    var list = document.getElementById('links-list');
    if (!list) return;
    list.innerHTML = '';
    links.forEach(function (l) {
      addLinkEntry(typeof l === 'string' ? l : (l.url || ''));
    });
  }

  function addLinkEntry(value) {
    var list = document.getElementById('links-list');
    if (!list) return;
    var div = document.createElement('div');
    div.className = 'link-entry';
    div.innerHTML =
      '<input type="url" placeholder="https://…" value="' + escapeHtml(value || '') + '" />' +
      '<button class="btn-remove-link"><svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg></button>';
    div.querySelector('.btn-remove-link').addEventListener('click', function () {
      div.remove();
    });
    list.appendChild(div);
  }

  // ── Save profile ───────────────────────────────────────────
  function initSaveProfile() {
    var btn = document.getElementById('btn-save-profile');
    if (!btn) return;
    btn.addEventListener('click', function () {
      var links = [];
      document.querySelectorAll('#links-list .link-entry input').forEach(function (inp) {
        if (inp.value.trim()) links.push(inp.value.trim());
      });
      var payload = {
        bio: getVal('field-bio'),
        links: links
      };
      btn.disabled = true;
      btn.textContent = 'Saving…';
      fetch('/api/profile/update', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      }).then(function (r) {
        var status = document.getElementById('save-status');
        if (r.ok) {
          status.textContent = '✓ Saved';
          status.classList.add('visible');
          setTimeout(function () { status.classList.remove('visible'); }, 2500);
        } else {
          status.textContent = 'Error saving';
          status.style.color = 'var(--danger)';
          status.classList.add('visible');
          setTimeout(function () { status.classList.remove('visible'); }, 2500);
        }
      }).finally(function () {
        btn.disabled = false;
        btn.textContent = 'Save changes';
      });
    });
  }

  // ── Theme switching ────────────────────────────────────────
  
  function applyTheme(theme) {
    var themes = {
      dark:     { '--bg-rgb':'8,8,18','--bg':'#080812','--bg-2':'#0d0d1a','--bg-3':'#111125','--bg-4':'#161630','--text':'#e8e8f0','--text-2':'#8888a8','--text-3':'#55556a' },
      midnight: { '--bg-rgb':'5,5,16','--bg':'#050510','--bg-2':'#08080f','--bg-3':'#0c0c18','--bg-4':'#101020','--text':'#c8c8e8','--text-2':'#6666aa','--text-3':'#444466' },
      oled:     { '--bg-rgb':'0,0,0','--bg':'#000000','--bg-2':'#050505','--bg-3':'#0a0a0a','--bg-4':'#0f0f0f','--text':'#ffffff','--text-2':'#888888','--text-3':'#444444' },
      light:    { '--bg-rgb':'244,244,248','--bg':'#f4f4f8','--bg-2':'#ebebf0','--bg-3':'#e0e0ea','--bg-4':'#d5d5e0','--text':'#111120','--text-2':'#555566','--text-3':'#999aaa' }
    };
    var vars = themes[theme] || themes.dark;
    Object.keys(vars).forEach(function(k) {
      document.documentElement.style.setProperty(k, vars[k]);
    });
    document.documentElement.setAttribute('data-theme', theme);
  }
  function initTheme() {
    document.querySelectorAll('.theme-option').forEach(function (opt) {
      opt.addEventListener('click', function () {
        document.querySelectorAll('.theme-option').forEach(function (o) { o.classList.remove('active'); });
        opt.classList.add('active');
        localStorage.setItem('rs_theme', opt.dataset.theme);
        applyTheme(opt.dataset.theme);
      });
    });
    document.querySelectorAll('.accent-swatch').forEach(function (sw) {
      sw.addEventListener('click', function () {
        document.querySelectorAll('.accent-swatch').forEach(function (s) { s.classList.remove('active'); });
        sw.classList.add('active');
        document.documentElement.style.setProperty('--accent', sw.dataset.color);
        localStorage.setItem('rs_accent', sw.dataset.color);
      });
    });
    // Restore theme
    var savedTheme = localStorage.getItem('rs_theme');
    if (savedTheme) {
      applyTheme(savedTheme);
      document.querySelectorAll('.theme-option').forEach(function(o) {
        o.classList.toggle('active', o.dataset.theme === savedTheme);
      });
    }
    // Restore accent
    var savedAccent = localStorage.getItem('rs_accent');
    if (savedAccent) {
      document.documentElement.style.setProperty('--accent', savedAccent);
      document.querySelectorAll('.accent-swatch').forEach(function (s) {
        s.classList.toggle('active', s.dataset.color === savedAccent);
      });
    }
  }

  // ── Font size slider ───────────────────────────────────────
  function initSliders() {
    var slider = document.getElementById('font-size-slider');
    var val = document.getElementById('font-size-val');
    if (!slider || !val) return;
    slider.addEventListener('input', function () {
      val.textContent = slider.value + 'px';
      document.documentElement.style.fontSize = slider.value + 'px';
    });
  }

  // ── Copy buttons ───────────────────────────────────────────
  function initCopyButtons() {
    document.querySelectorAll('.btn-copy').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var sourceId = btn.dataset.copyId;
        var source = document.getElementById(sourceId);
        if (!source) return;
        navigator.clipboard.writeText(source.textContent.trim()).then(function () {
          btn.textContent = 'Copied!';
          btn.classList.add('copied');
          setTimeout(function () {
            btn.textContent = 'Copy';
            btn.classList.remove('copied');
          }, 1500);
        });
      });
    });
  }

  // ── Add link button ────────────────────────────────────────
  function initLinkButtons() {
    var btn = document.getElementById('btn-add-link');
    if (btn) btn.addEventListener('click', function () { addLinkEntry(''); });
  }

  // ── Danger zone ────────────────────────────────────────────

  function initPrivacySettings() {
    var map = [
      ["showOnline", 335],
      ["publicProfile", 339],
      ["showJoinDate", 343],
      ["showDeveloperBadge", 347],
      ["searchableByUsername", 356],
      ["searchableByEmail", 360],
      ["allowFriendRequests", 369],
      ["allowStrangerDms", 373]
    ];

    var inputs = Array.from(document.querySelectorAll("#section-privacy input[type='checkbox']"));

    fetch("/api/settings/privacy", { credentials: "include" })
      .then(function (r) { return r.ok ? r.json() : {}; })
      .then(function (data) {
        map.forEach(function (pair, i) {
          if (inputs[i] && Object.prototype.hasOwnProperty.call(data, pair[0])) {
            inputs[i].checked = !!data[pair[0]];
          }
        });
      });

    inputs.forEach(function (input, i) {
      input.addEventListener("change", function () {
        var payload = {};
        map.forEach(function (pair, idx) {
          if (inputs[idx]) payload[pair[0]] = !!inputs[idx].checked;
        });

        fetch("/api/settings/privacy", {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload)
        }).catch(function (err) {
          console.error("Privacy save failed:", err);
        });
      });
    });
  }


  function initNotificationSettings() {
    var map = [
      "desktopNotifications",
      "soundEffects",
      "marketingEmails",
      "mentionsOnly",
      "dmsOnly",
      "securityAlerts"
    ];

    var inputs = Array.from(document.querySelectorAll("#section-notifications input[type='checkbox']"));

    fetch("/api/settings/notifications", { credentials: "include" })
      .then(function (r) { return r.ok ? r.json() : {}; })
      .then(function (data) {
        map.forEach(function (key, i) {
          if (inputs[i] && Object.prototype.hasOwnProperty.call(data, key)) {
            inputs[i].checked = !!data[key];
          }
        });
      });

    inputs.forEach(function (input) {
      input.addEventListener("change", function () {
        var payload = {};
        map.forEach(function (key, idx) {
          if (inputs[idx]) payload[key] = !!inputs[idx].checked;
        });

        fetch("/api/settings/notifications", {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload)
        }).catch(function (err) {
          console.error("Notification save failed:", err);
        });
      });
    });
  }


  function initAppearanceSettings() {
    var themeOptions = Array.from(document.querySelectorAll(".theme-option"));
    var accentOptions = Array.from(document.querySelectorAll(".accent-swatch"));
    var appearanceInputs = Array.from(document.querySelectorAll("#section-appearance input[type='checkbox']"));

    function collectAppearance() {
      var activeTheme = document.querySelector(".theme-option.active");
      var activeAccent = document.querySelector(".accent-swatch.active");

      return {
        theme: activeTheme ? activeTheme.dataset.theme : (localStorage.getItem("rs_theme") || "dark"),
        accent: activeAccent ? activeAccent.dataset.color : (localStorage.getItem("rs_accent") || "purple"),
        compactMode: !!(appearanceInputs[0] && appearanceInputs[0].checked),
        reducedMotion: !!(appearanceInputs[1] && appearanceInputs[1].checked)
      };
    }

    function saveAppearance() {
      fetch("/api/settings/appearance", {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(collectAppearance())
      }).catch(function (err) {
        console.error("Appearance save failed:", err);
      });
    }

    fetch("/api/settings/appearance", { credentials: "include" })
      .then(function (r) { return r.ok ? r.json() : {}; })
      .then(function (data) {
        if (data.theme) {
          localStorage.setItem("rs_theme", data.theme);
          document.documentElement.setAttribute("data-theme", data.theme);
          themeOptions.forEach(function (o) {
            o.classList.toggle("active", o.dataset.theme === data.theme);
          });
        }

        if (data.accent) {
          localStorage.setItem("rs_accent", data.accent);
          document.documentElement.style.setProperty("--accent", data.accent);
          accentOptions.forEach(function (o) {
            o.classList.toggle("active", o.dataset.color === data.accent);
          });
        }

        if (appearanceInputs[0] && Object.prototype.hasOwnProperty.call(data, "compactMode")) {
          appearanceInputs[0].checked = !!data.compactMode;
        }
        if (appearanceInputs[1] && Object.prototype.hasOwnProperty.call(data, "reducedMotion")) {
          appearanceInputs[1].checked = !!data.reducedMotion;
        }
      });

    themeOptions.forEach(function (opt) {
      opt.addEventListener("click", saveAppearance);
    });

    accentOptions.forEach(function (opt) {
      opt.addEventListener("click", saveAppearance);
    });

    appearanceInputs.forEach(function (input) {
      input.addEventListener("change", saveAppearance);
    });
  }


  function initDeveloperSettings() {
    var map = [
      "developerMode",
      "copyUserIdsOnClick",
      "debugLogs"
    ];

    var inputs = Array.from(document.querySelectorAll("#section-developer input[type='checkbox']"));

    fetch("/api/settings/developer", { credentials: "include" })
      .then(function (r) { return r.ok ? r.json() : {}; })
      .then(function (data) {
        map.forEach(function (key, i) {
          if (inputs[i] && Object.prototype.hasOwnProperty.call(data, key)) {
            inputs[i].checked = !!data[key];
          }
        });
      });

    inputs.forEach(function (input) {
      input.addEventListener("change", function () {
        var payload = {};
        map.forEach(function (key, idx) {
          if (inputs[idx]) payload[key] = !!inputs[idx].checked;
        });

        fetch("/api/settings/developer", {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload)
        }).catch(function (err) {
          console.error("Developer settings save failed:", err);
        });
      });
    });
  }

  function initDanger() {
    var btnLogoutAll = document.getElementById('btn-logout-all');
    if (btnLogoutAll) {
      btnLogoutAll.addEventListener('click', function () {
        if (!confirm('Log out all other sessions?')) return;
        fetch('/api/auth/logout-all', { method: 'POST' })
          .then(function (r) { if (r.ok) alert('All other sessions logged out.'); });
      });
    }
    var btnLogoutEverywhere = document.getElementById('btn-logout-everywhere');
    if (btnLogoutEverywhere) {
      btnLogoutEverywhere.addEventListener('click', function () {
        if (!confirm('This will log you out everywhere, including here. Continue?')) return;
        fetch('/api/auth/logout-all', { method: 'POST' })
          .then(function () { window.location.href = '/login/'; });
      });
    }
    var btnDelete = document.getElementById('btn-delete-account');
    if (btnDelete) {
      btnDelete.addEventListener('click', function () {
        var confirm1 = confirm('Are you absolutely sure you want to delete your account? This cannot be undone.');
        if (!confirm1) return;
        var typed = prompt('Type DELETE to confirm:');
        if (typed !== 'DELETE') return;
        fetch('/api/account/delete', { method: 'DELETE' })
          .then(function (r) {
            if (r.ok) { alert('Account deleted.'); window.location.href = '/'; }
            else alert('Failed to delete account. Please contact support.');
          });
      });
    }
  }

  // ── Helpers ────────────────────────────────────────────────
  function setVal(id, val) {
    var el = document.getElementById(id);
    if (!el) return;
    if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') el.value = val;
  }
  function getVal(id) {
    var el = document.getElementById(id);
    return el ? el.value : '';
  }
  function setCode(id, val) {
    var el = document.getElementById(id);
    if (el) el.textContent = val;
  }
  function show(id) {
    var el = document.getElementById(id);
    if (el) el.classList.remove('hidden');
  }
  function escapeHtml(str) {
    if (!str) return '';
    return str.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
  }

  // ── Init ───────────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', function () {
    initStars();
    initNav();
    loadMe();
    initSaveProfile();
    initTheme();
    initSliders();
    initCopyButtons();
    initLinkButtons();
    initPrivacySettings();
    initNotificationSettings();
    initAppearanceSettings();
    initDeveloperSettings();
    initDanger();
  });
})();
