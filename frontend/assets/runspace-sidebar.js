(function () {
  function normalize(path) {
    if (!path) return "/";
    if (!path.startsWith("/")) path = "/" + path;
    if (!path.endsWith("/")) path += "/";
    return path;
  }

  document.addEventListener("DOMContentLoaded", function () {
    var current = normalize(window.location.pathname);
    var links = document.querySelectorAll(".side .nav-item");

    links.forEach(function (a) {
      a.classList.remove("active");
      a.removeAttribute("aria-current");

      var href = a.getAttribute("href") || "";
      if (href.startsWith("http")) return;

      var linkPath = normalize(href.split("?")[0].split("#")[0]);

      if (
        linkPath === current ||
        (linkPath !== "/" && current.startsWith(linkPath))
      ) {
        a.classList.add("active");
        a.setAttribute("aria-current", "page");
      }
    });
  });
})();

/* ===== Force sidebar footer to viewport corner ===== */
document.addEventListener("DOMContentLoaded", function () {
  var footer = document.querySelector(".side-footer");
  if (!footer) return;

  // Move it outside the scrollable sidebar so it cannot scroll with menu links.
  if (footer.parentElement && footer.parentElement !== document.body) {
    document.body.appendChild(footer);
  }

  footer.style.position = "fixed";
  footer.style.left = "16px";
  footer.style.bottom = "14px";
  footer.style.width = "190px";
  footer.style.height = "auto";
  footer.style.zIndex = "9999";
  footer.style.margin = "0";
  footer.style.padding = "0";
  footer.style.borderTop = "0";
  footer.style.background = "transparent";
  footer.style.pointerEvents = "none";
});
