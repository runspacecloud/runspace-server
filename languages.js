// === RunSpace Language Selector ===
var LANGUAGES = [
  {code:"sv",flag:"SE",label:"Svenska"},
  {code:"en",flag:"GB",label:"English"},
  {code:"fr",flag:"FR",label:"Fran\u00e7ais"},
  {code:"ru",flag:"RU",label:"\u0420\u0443\u0441\u0441\u043a\u0438\u0439"}
];

function populateLangSelector(containerId, currentVal) {
  var el = document.getElementById(containerId);
  if (!el) return;
  var selected = (currentVal || "").split(",").filter(function(x) { return x; });
  var h = "";
  LANGUAGES.forEach(function(l) {
    var checked = selected.indexOf(l.code) >= 0;
    h += '<label style="display:flex;align-items:center;gap:8px;padding:8px 10px;border:1px solid ' + (checked ? 'var(--blue,#3b82f6)' : 'var(--border,#1e293b)') + ';border-radius:8px;margin-bottom:4px;cursor:pointer;background:' + (checked ? 'rgba(59,130,246,0.08)' : 'transparent') + '">';
    h += '<input type="checkbox" class="lang-cb" value="' + l.code + '"' + (checked ? ' checked' : '') + ' style="accent-color:var(--blue,#3b82f6)">';
    h += '<span>' + codeToFlag(l.flag) + ' ' + l.label + '</span></label>';
  });
  el.innerHTML = h;
  el.querySelectorAll(".lang-cb").forEach(function(cb) {
    cb.addEventListener("change", function() {
      populateLangSelector(containerId, getSelectedLangs(containerId));
    });
  });
}

function getSelectedLangs(containerId) {
  var el = document.getElementById(containerId);
  if (!el) return "";
  var vals = [];
  el.querySelectorAll(".lang-cb:checked").forEach(function(cb) { vals.push(cb.value); });
  return vals.join(",");
}

async function saveLangs() {
  var val = getSelectedLangs("langSelector");
  var bioEl = document.getElementById("bioInput");
  var natEl = document.getElementById("natSelect");
  try {
    var r = await fetch("/api/profile/update", {
      method: "POST", credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ bio: bioEl ? bioEl.value.trim() : "", nationality: natEl ? natEl.value : "", languages: val })
    });
    if (!r.ok) { if (typeof toast === "function") toast("Kunde inte spara", "err"); return; }
    if (typeof toast === "function") toast("Spr\u00e5k sparade");
    var ml = document.getElementById("metaLangs");
    if (ml) {
      ml.innerHTML = val ? val.split(",").map(function(c) {
        var l = LANGUAGES.find(function(x) { return x.code === c; });
        return l ? codeToFlag(l.flag) : "";
      }).join(" ") : "";
    }
  } catch(e) { if (typeof toast === "function") toast("N\u00e4tverksfel", "err"); }
}

(function() {
  function tryInit() {
    if (typeof me !== "undefined" && me) {
      populateLangSelector("langSelector", me.languages || "");
      var ml = document.getElementById("metaLangs");
      if (ml && me.languages) {
        ml.innerHTML = me.languages.split(",").map(function(c) {
          var l = LANGUAGES.find(function(x) { return x.code === c; });
          return l ? codeToFlag(l.flag) : "";
        }).join(" ");
      }
      var btn = document.getElementById("saveLangsBtn");
      if (btn) btn.onclick = saveLangs;
    } else { setTimeout(tryInit, 500); }
  }
  if (document.readyState === "complete") setTimeout(tryInit, 600);
  else document.addEventListener("DOMContentLoaded", function() { setTimeout(tryInit, 1600); });
})();
