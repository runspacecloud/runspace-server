(function () {
  'use strict';

  const API_URL = '/api/news';

  // ── State ──────────────────────────────────────────────────
  let allItems  = [];
  let activeTag = 'all';
  let isAdmin   = false;

  // ── DOM refs ───────────────────────────────────────────────
  const feed       = document.getElementById('news-feed');
  const emptyEl    = document.getElementById('news-empty');
  const errorEl    = document.getElementById('news-error');
  const retryBtn   = document.getElementById('retry-btn');
  const filterBtns = document.querySelectorAll('.filter-btn');
  const adminPanel = document.getElementById('admin-panel');
  const adminForm  = document.getElementById('admin-form');
  const adminStatus = document.getElementById('admin-status');

  // ── Tag config ─────────────────────────────────────────────
  const TAG_LABELS = {
    launch:      'Launch',
    update:      'Update',
    security:    'Security',
    fix:         'Fix',
    improvement: 'Improve',
  };

  const TAG_CSS = {
    launch:      'tag-new',
    update:      'tag-improve',
    security:    'tag-security',
    fix:         'tag-fix',
    improvement: 'tag-improve',
  };

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
  function formatDate(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    return d.toLocaleDateString('en-SE', { year: 'numeric', month: 'short', day: 'numeric' });
  }

  function escapeHtml(str) {
    if (!str) return '';
    return str
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // ── Render feed ────────────────────────────────────────────
  function renderItems(items) {
    feed.innerHTML = '';
    emptyEl.classList.add('hidden');
    errorEl.classList.add('hidden');

    if (!items.length) {
      emptyEl.classList.remove('hidden');
      return;
    }

    items.forEach(function (item, i) {
      const tag      = (item.tag || 'update').toLowerCase();
      const cssClass = TAG_CSS[tag] || 'tag-improve';
      const label    = TAG_LABELS[tag] || tag;
      const title    = escapeHtml(item.titleEn || item.titleSv || '');
      const body     = escapeHtml(item.bodyEn  || item.bodySv  || '');
      const date     = formatDate(item.createdAt);

      const el = document.createElement('div');
      el.className = 'news-item';
      el.dataset.id = item.id;
      el.style.animationDelay = (i * 0.04) + 's';

      el.innerHTML =
        '<div class="news-date-col"><span class="news-date">' + date + '</span></div>' +
        '<div class="news-content">' +
          '<div class="news-head">' +
            '<span class="news-tag ' + cssClass + '">' + label + '</span>' +
            '<span class="news-title">' + title + '</span>' +
            (isAdmin
              ? '<button class="news-delete-btn" data-id="' + item.id + '" title="Delete">' +
                  '<svg viewBox="0 0 24 24"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4h6v2"/></svg>' +
                '</button>'
              : '') +
          '</div>' +
          (body ? '<p class="news-body">' + body + '</p>' : '') +
        '</div>';

      feed.appendChild(el);
    });

    // Delete buttons
    if (isAdmin) {
      feed.querySelectorAll('.news-delete-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
          deleteItem(parseInt(btn.dataset.id, 10));
        });
      });
    }
  }

  function applyFilter() {
    const filtered = activeTag === 'all'
      ? allItems
      : allItems.filter(function (i) {
          return (i.tag || '').toLowerCase() === activeTag;
        });
    renderItems(filtered);
  }

  // ── Fetch ──────────────────────────────────────────────────
  function loadNews() {
    feed.innerHTML =
      '<div class="news-skeleton">' +
        '<div class="skel-item"></div>' +
        '<div class="skel-item"></div>' +
        '<div class="skel-item"></div>' +
      '</div>';
    emptyEl.classList.add('hidden');
    errorEl.classList.add('hidden');

    fetch(API_URL)
      .then(function (res) {
        if (!res.ok) throw new Error('Bad response');
        return res.json();
      })
      .then(function (data) {
        allItems = Array.isArray(data) ? data : [];
        applyFilter();
      })
      .catch(function () {
        feed.innerHTML = '';
        errorEl.classList.remove('hidden');
      });
  }

  // ── Delete ─────────────────────────────────────────────────
  function deleteItem(id) {
    if (!confirm('Delete this update?')) return;

    fetch(API_URL + '/' + id, { method: 'DELETE' })
      .then(function (res) {
        if (!res.ok) throw new Error('Delete failed');
        allItems = allItems.filter(function (i) { return i.id !== id; });
        applyFilter();
      })
      .catch(function () {
        alert('Could not delete. Are you logged in as mx403?');
      });
  }

  // ── Admin: check session ───────────────────────────────────
  function checkAdmin() {
    fetch('/api/me')
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (data) {
        if (data && data.username && data.username.toLowerCase() === 'mx403') {
          isAdmin = true;
          if (adminPanel) adminPanel.classList.remove('hidden');
        }
      })
      .catch(function () {});
  }

  // ── Admin: post new item ───────────────────────────────────
  function initAdminForm() {
    if (!adminForm) return;

    adminForm.addEventListener('submit', function (e) {
      e.preventDefault();
      adminStatus.textContent = '';

      var titleEn = adminForm.querySelector('[name="titleEn"]').value.trim();
      var titleSv = adminForm.querySelector('[name="titleSv"]').value.trim();
      var bodyEn  = adminForm.querySelector('[name="bodyEn"]').value.trim();
      var bodySv  = adminForm.querySelector('[name="bodySv"]').value.trim();
      var tag     = adminForm.querySelector('[name="tag"]').value;

      if (!titleSv || !bodySv) {
        adminStatus.textContent = 'Swedish title and body are required.';
        adminStatus.className = 'admin-status error';
        return;
      }

      var submitBtn = adminForm.querySelector('.admin-submit');
      submitBtn.disabled = true;
      submitBtn.textContent = 'Posting...';

      fetch(API_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          TitleSv: titleSv,
          TitleEn: titleEn || titleSv,
          BodySv:  bodySv,
          BodyEn:  bodyEn || bodySv,
          Tag:     tag,
        }),
      })
        .then(function (res) {
          if (!res.ok) throw new Error('Post failed');
          return res.json();
        })
        .then(function () {
          adminForm.reset();
          adminStatus.textContent = 'Posted!';
          adminStatus.className = 'admin-status ok';
          loadNews();
          setTimeout(function () { adminStatus.textContent = ''; }, 3000);
        })
        .catch(function () {
          adminStatus.textContent = 'Failed to post. Make sure you are logged in.';
          adminStatus.className = 'admin-status error';
        })
        .finally(function () {
          submitBtn.disabled = false;
          submitBtn.textContent = 'Post update';
        });
    });
  }

  // ── Filter buttons ─────────────────────────────────────────
  filterBtns.forEach(function (btn) {
    btn.addEventListener('click', function () {
      filterBtns.forEach(function (b) { b.classList.remove('active'); });
      btn.classList.add('active');
      activeTag = btn.dataset.tag;
      applyFilter();
    });
  });

  if (retryBtn) {
    retryBtn.addEventListener('click', loadNews);
  }

  // ── Init ───────────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', function () {
    initStars();
    checkAdmin();
    initAdminForm();
    loadNews();
  });
})();
