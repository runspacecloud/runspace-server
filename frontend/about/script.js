(function () {
  'use strict';

  // ── Starfield ──────────────────────────────────────────────
  function initStars() {
    const canvas = document.getElementById('stars');
    if (!canvas) return;

    const COUNT = 40;

    for (let i = 0; i < COUNT; i++) {
      const dot = document.createElement('div');
      dot.className = 'rs-star-dot';

      const size     = Math.random() * 1.4 + 0.3;
      const duration = Math.random() * 35 + 25;
      const delay    = -(Math.random() * 35);

      dot.style.cssText = [
        `width:${size}px`,
        `height:${size}px`,
        `left:${Math.random() * 100}%`,
        `top:${Math.random() * 100}%`,
        `opacity:${(Math.random() * 0.22 + 0.04).toFixed(3)}`,
        `animation-duration:${duration}s`,
        `animation-delay:${delay}s`,
      ].join(';');

      canvas.appendChild(dot);
    }
  }

  // ── Init ───────────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', function () {
    initStars();
  });
})();
