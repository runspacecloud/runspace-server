#!/usr/bin/env python3
"""
Patch chatt.html for mobile-friendly layout.
- Replaces the @media(max-width:980px) rule so sidebar/DM list shows by default on mobile
- Adds a mobile back button in the chat header
- Adds JS to handle mobile view switching (DM list <-> chat)
- Removes hamburger menu dependency on mobile

Usage: python3 patch_mobile.py /var/www/runspace/frontend/chatt.html
"""
import sys, re, shutil, os

if len(sys.argv) < 2:
    print("Usage: python3 patch_mobile.py <path-to-chatt.html>")
    sys.exit(1)

path = sys.argv[1]
if not os.path.isfile(path):
    print(f"File not found: {path}")
    sys.exit(1)

# Backup
shutil.copy2(path, path + ".bak")
print(f"Backup: {path}.bak")

with open(path, "r", encoding="utf-8") as f:
    html = f.read()

changes = 0

# ─── 1. Replace the @media(max-width:980px) rule ───
old_media = (
    "@media(max-width:980px){"
    ".app-layout{grid-template-columns:1fr}"
    ".server-list,.sidebar{position:fixed;left:0;top:var(--topbar-height);bottom:0;"
    "z-index:60;transform:translateX(-100%);transition:transform .2s ease}"
    ".server-list{width:56px}"
    ".sidebar{width:230px;left:56px}"
    "body.mobile-sidebar-open .server-list,"
    "body.mobile-sidebar-open .sidebar{transform:translateX(0)}"
    ".mobile-menu-btn{display:inline-block}}"
)

new_media = (
    "@media(max-width:980px){\n"
    "  .app-layout{grid-template-columns:1fr;grid-template-rows:1fr}\n"
    "  .server-list{display:none}\n"
    "  .sidebar{position:relative;width:100%;height:calc(100vh - var(--topbar-height));border-right:none;display:flex}\n"
    "  .chat{display:none}\n"
    "  body.mobile-chat-open .sidebar{display:none!important}\n"
    "  body.mobile-chat-open .chat{display:flex!important}\n"
    "  .mobile-menu-btn{display:none}\n"
    "  .mobile-back-btn{display:inline-flex!important}\n"
    "  .chat-head{padding:0 10px}\n"
    "  .composer{padding:6px 8px 8px}\n"
    "}"
)

if old_media in html:
    html = html.replace(old_media, new_media, 1)
    changes += 1
    print("[OK] Replaced @media(max-width:980px) rule")
else:
    # Try a regex approach for whitespace differences
    pattern = r'@media\(max-width:\s*980px\)\s*\{[^}]*\.mobile-menu-btn\{display:inline-block\}\s*\}'
    m = re.search(pattern, html)
    if m:
        html = html[:m.start()] + new_media + html[m.end():]
        changes += 1
        print("[OK] Replaced @media(max-width:980px) rule (regex)")
    else:
        print("[WARN] Could not find @media(max-width:980px) rule to replace!")
        print("       You may need to replace it manually.")

# ─── 2. Add mobile-back-btn CSS ───
mobile_back_css = (
    "\n.mobile-back-btn{display:none!important;align-items:center;justify-content:center;"
    "width:32px;height:32px;border-radius:7px;border:1px solid var(--border);"
    "background:none;color:var(--dim);cursor:pointer;font-size:16px;flex-shrink:0;margin-right:4px}"
    "\n.mobile-back-btn:hover{background:rgba(255,255,255,.06);color:var(--text)}\n"
)

# Insert before the @media(max-width:720px) rule or before closing </style>
if ".mobile-back-btn{" not in html:
    insert_before = "@media(max-width:720px)"
    if insert_before in html:
        idx = html.index(insert_before)
        html = html[:idx] + mobile_back_css + "\n" + html[idx:]
        changes += 1
        print("[OK] Added .mobile-back-btn CSS")
    else:
        # Fallback: insert before first </style>
        idx = html.index("</style>")
        html = html[:idx] + mobile_back_css + html[idx:]
        changes += 1
        print("[OK] Added .mobile-back-btn CSS (before </style>)")
else:
    print("[SKIP] .mobile-back-btn CSS already exists")

# ─── 3. Add back button in chat-head ───
chat_icon_marker = '<span class="chat-head-icon" id="chatIcon">@</span>'
back_btn_html = '<button type="button" class="mobile-back-btn" id="mobileBackBtn" title="Tillbaka">\xe2\x86\x90</button>'

if "mobileBackBtn" not in html:
    if chat_icon_marker in html:
        html = html.replace(
            chat_icon_marker,
            back_btn_html + "\n" + chat_icon_marker,
            1
        )
        changes += 1
        print("[OK] Added mobile back button in chat-head")
    else:
        print("[WARN] Could not find chat-head-icon marker!")
else:
    print("[SKIP] Mobile back button already exists")

# ─── 4. Add mobile JS before </body> ───
mobile_js = '''
<script>
/* ── Mobile: DM list / Chat view switching ── */
(function(){
  var backBtn = document.getElementById('mobileBackBtn');
  if(backBtn){
    backBtn.addEventListener('click', function(){
      document.body.classList.remove('mobile-chat-open');
    });
  }

  // Wrap activateConvo to switch to chat view on mobile
  var _mobileWrap = null;
  function patchActivate(){
    if(typeof activateConvo !== 'function') return;
    if(_mobileWrap) return; // already patched
    var _orig = activateConvo;
    _mobileWrap = true;
    activateConvo = async function(peer){
      var result = await _orig(peer);
      if(window.innerWidth <= 980){
        document.body.classList.add('mobile-chat-open');
      }
      return result;
    };
  }

  // Patch after everything is loaded
  if(document.readyState === 'complete'){
    setTimeout(patchActivate, 500);
  } else {
    window.addEventListener('load', function(){ setTimeout(patchActivate, 500); });
  }

  // Also handle conv-item clicks directly as backup
  document.addEventListener('click', function(e){
    var item = e.target.closest('.conv-item');
    if(item && window.innerWidth <= 980){
      setTimeout(function(){
        document.body.classList.add('mobile-chat-open');
      }, 100);
    }
  });
})();
</script>
'''

if "mobile-chat-open" not in html.split("</body>")[0].split("<script>")[-1] if "</body>" in html else "":
    # Check if our mobile JS is already there
    pass

if "mobileBackBtn" in html and "mobile-chat-open" not in html.split("</body>")[-2].split("</script>")[-1] if "</body>" in html else True:
    # Simpler check: if our specific script block isn't there yet
    if "Mobile: DM list / Chat view switching" not in html:
        body_close = html.rfind("</body>")
        if body_close >= 0:
            html = html[:body_close] + mobile_js + "\n" + html[body_close:]
            changes += 1
            print("[OK] Added mobile view-switching JS")
        else:
            print("[WARN] Could not find </body> to insert JS!")
    else:
        print("[SKIP] Mobile JS already exists")

# ─── Write output ───
with open(path, "w", encoding="utf-8") as f:
    f.write(html)

print(f"\nDone! {changes} change(s) applied to {path}")
print(f"Backup saved as {path}.bak")
