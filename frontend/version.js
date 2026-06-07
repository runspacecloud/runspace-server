(function () {
  function apply(v) {
    const label = v.display || v.version || 'v2.4';
    document.querySelectorAll('.mt-ver, [data-rs-version]').forEach(el => {
      el.textContent = label;
    });
    document.documentElement.dataset.rsVersion = v.semver || label;
  }
  fetch('/version.json', { cache: 'no-store' })
    .then(r => r.ok ? r.json() : null)
    .then(v => { if (v) apply(v); })
    .catch(() => {});
})();
