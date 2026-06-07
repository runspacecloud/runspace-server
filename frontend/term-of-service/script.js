// ── Clear any stale data on page load ──
try {
  sessionStorage.removeItem("_cached_user");
} catch {}

// ── Device Token (persistent per browser) ──
(function () {
  let tok = localStorage.getItem("rs_device_token");

  if (!tok) {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    tok =
      "web-" +
      Array.from(bytes)
        .map((b) => b.toString(16).padStart(2, "0"))
        .join("");

    localStorage.setItem("rs_device_token", tok);
  }

  window._rsDeviceToken = tok;

  const originalFetch = window.fetch;

  window.fetch = function (url, opts = {}) {
    opts.headers = opts.headers || {};

    if (opts.headers instanceof Headers) {
      opts.headers.set("X-Device-Token", tok);
      opts.headers.set("X-Device-Name", navigator.userAgent.slice(0, 50));
    } else if (typeof opts.headers === "object" && !Array.isArray(opts.headers)) {
      opts.headers["X-Device-Token"] = tok;
      opts.headers["X-Device-Name"] = navigator.userAgent.slice(0, 50);
    }

    return originalFetch(url, opts);
  };
})();

// ── Starfield ──
(function () {
  const canvas = document.getElementById("rsStarCanvas");
  if (!canvas) return;

  const count = 60;

  for (let i = 0; i < count; i++) {
    const dot = document.createElement("div");
    dot.className = "rs-star-dot";

    const size = Math.random() * 2 + 0.5;
    const duration = Math.random() * 30 + 20;
    const delay = Math.random() * -50;
    const opacity = Math.random() * 0.5 + 0.2;

    dot.style.width = size + "px";
    dot.style.height = size + "px";
    dot.style.left = Math.random() * 100 + "%";
    dot.style.background = Math.random() > 0.85 ? "#3b7dff" : "#eaf0ff";
    dot.style.opacity = opacity;
    dot.style.animationDuration = duration + "s";
    dot.style.animationDelay = delay + "s";
    dot.style.boxShadow = size > 1.5 ? "0 0 " + size * 2 + "px currentColor" : "none";

    canvas.appendChild(dot);
  }
})();

// ── Mobile menu helpers ──
function openMenu() {
  document.body.classList.add("menu-open");
}

function closeMenu() {
  document.body.classList.remove("menu-open");
}

function toggleMenu() {
  document.body.classList.toggle("menu-open");
}

window.openMenu = openMenu;
window.closeMenu = closeMenu;
window.toggleMenu = toggleMenu;

// ── Escape closes mobile menu ──
document.addEventListener("keydown", function (e) {
  if (e.key === "Escape") closeMenu();
});

// ── Close menu on nav click mobile ──
document.querySelectorAll(".side .nav-item").forEach(function (el) {
  el.addEventListener("click", function () {
    if (window.innerWidth <= 900) closeMenu();
  });
});

// ── Language buttons placeholder ──
document.querySelectorAll(".lang-btn").forEach(function (btn) {
  btn.addEventListener("click", function () {
    document.querySelectorAll(".lang-btn").forEach((b) => b.classList.remove("active"));
    btn.classList.add("active");
  });
});
