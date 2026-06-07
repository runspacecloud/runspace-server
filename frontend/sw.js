/**
 * RunSpace Service Worker
 * ------------------------
 * Strategy:
 *   - HTML pages: network-first, fall back to cache, then offline page
 *   - Static assets (css/js/img/fonts): stale-while-revalidate
 *   - API calls (/api/*, /hub/*): never cache (realtime)
 *   - SignalR (/chathub): bypass entirely
 *
 * Bump CACHE_VERSION when you deploy to force clients to refresh.
 */

const CACHE_VERSION = 'v1.0.4';
const STATIC_CACHE = `runspace-static-${CACHE_VERSION}`;
const RUNTIME_CACHE = `runspace-runtime-${CACHE_VERSION}`;

// App shell — absolute minimum to boot the UI offline
const APP_SHELL = [
  '/',
  '/index.html',
  '/offline.html',
  '/manifest.webmanifest',
  '/icons/icon-192.png',
  '/icons/icon-512.png',
  // Add your core CSS/JS bundles here once you know them, e.g.:
  // '/css/main.css',
  // '/js/i18n.js',
];

// Never cache these paths — they need to hit the server every time
const NEVER_CACHE = [
  '/api/',
  '/ws/',        // SignalR hub endpoints
  '/ws/chathub',     // SignalR main connection
  '/auth/',
  '/admin/',
  '/security.html', // admin panel, always fresh
];

// Install: pre-cache the app shell
self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(STATIC_CACHE)
      .then((cache) => cache.addAll(APP_SHELL))
      .then(() => self.skipWaiting())
      .catch((err) => console.warn('[SW] Install failed:', err))
  );
});

// Activate: clean up old caches
self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(
        keys
          .filter((key) => key.startsWith('runspace-') && ![STATIC_CACHE, RUNTIME_CACHE].includes(key))
          .map((key) => caches.delete(key))
      )
    ).then(() => self.clients.claim())
  );
});

// Fetch handler
self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);

  // Only handle same-origin GET requests
  if (request.method !== 'GET' || url.origin !== self.location.origin) return;

  // Never cache API / realtime / auth
  if (NEVER_CACHE.some((path) => url.pathname.startsWith(path))) {
    return; // let the browser handle it normally
  }

  // SignalR websocket upgrade — don't touch
  if (request.headers.get('upgrade') === 'websocket') return;

  // HTML navigation: network-first
  if (request.mode === 'navigate' || request.headers.get('accept')?.includes('text/html')) {
    event.respondWith(networkFirst(request));
    return;
  }

  // Everything else (css, js, img, fonts): stale-while-revalidate
  event.respondWith(staleWhileRevalidate(request));
});

async function networkFirst(request) {
  try {
    const response = await fetch(request);
    // Cache successful HTML responses
    if (response.ok) {
      const cache = await caches.open(RUNTIME_CACHE);
      cache.put(request, response.clone());
    }
    return response;
  } catch (err) {
    const cached = await caches.match(request);
    if (cached) return cached;
    // Last resort: offline page
    const offline = await caches.match('/offline.html');
    if (offline) return offline;
    return new Response('Offline', { status: 503, statusText: 'Offline' });
  }
}

async function staleWhileRevalidate(request) {
  const cache = await caches.open(RUNTIME_CACHE);
  const cached = await cache.match(request);
  const fetchPromise = fetch(request)
    .then((response) => {
      if (response.ok) cache.put(request, response.clone());
      return response;
    })
    .catch(() => cached); // on network fail, fall through to cache
  return cached || fetchPromise;
}

// ----- Push notifications (future) -----
// When you wire up Web Push, uncomment this and add VAPID keys in manifest.
/*
self.addEventListener('push', (event) => {
  const data = event.data?.json() ?? {};
  event.waitUntil(
    self.registration.showNotification(data.title || 'RunSpace', {
      body: data.body,
      icon: '/icons/icon-192.png',
      badge: '/icons/icon-96.png',
      tag: data.tag || 'message',
      data: { url: data.url || '/chatt' },
    })
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = event.notification.data?.url || '/';
  event.waitUntil(
    clients.matchAll({ type: 'window' }).then((windowClients) => {
      for (const client of windowClients) {
        if (client.url.includes(url) && 'focus' in client) return client.focus();
      }
      return clients.openWindow(url);
    })
  );
});
*/

// Message channel for manual cache busting from the page
self.addEventListener('message', (event) => {
  if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
  if (event.data?.type === 'CLEAR_CACHE') {
    caches.keys().then((keys) => keys.forEach((k) => caches.delete(k)));
  }
});
