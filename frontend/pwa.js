(function registerPWA() {
  if (!('serviceWorker' in navigator)) return;
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js', { scope: '/' })
      .then(reg => {
        console.log('[PWA] SW registered:', reg.scope);
        try { reg.update(); } catch (_) {}
        if (navigator.serviceWorker.controller) {
          navigator.serviceWorker.controller.postMessage({ type: 'CLEAR_CACHE' });
        }
      })
      .catch(err => console.warn('[PWA] SW failed:', err));
  });
  let deferredPrompt;
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferredPrompt = e;
    window.runspaceInstallPrompt = async () => {
      if (!deferredPrompt) return false;
      deferredPrompt.prompt();
      const { outcome } = await deferredPrompt.userChoice;
      deferredPrompt = null;
      return outcome === 'accepted';
    };
    window.dispatchEvent(new CustomEvent('pwa-installable'));
  });
  window.addEventListener('appinstalled', () => { deferredPrompt = null; });
  if (window.matchMedia('(display-mode: standalone)').matches) {
    document.documentElement.classList.add('pwa-standalone');
  }
})();
