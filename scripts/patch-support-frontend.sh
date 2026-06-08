#!/bin/bash
# ═══════════════════════════════════════════════
# patch-support-frontend.sh - Phase 2
# Deploys my-tickets.html + updates support.html sidebar
# ═══════════════════════════════════════════════

set -e

WWWROOT="/root/RunSpace/NewServer/publish/wwwroot"
SUPPORT_HTML="$WWWROOT/support.html"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'

echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo -e "${BLUE}  Support Inbox - Phase 2 (User Frontend)${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════${NC}"

# ── Step 1: Verify files ──
[ ! -f "./my-tickets.html" ] && { echo -e "${RED}✗ my-tickets.html not found in current dir${NC}"; exit 1; }
[ ! -d "$WWWROOT" ] && { echo -e "${RED}✗ wwwroot not found${NC}"; exit 1; }
[ ! -f "$SUPPORT_HTML" ] && { echo -e "${RED}✗ support.html not found${NC}"; exit 1; }
echo -e "${GREEN}✓${NC} Files present"

# ── Step 2: Backup ──
cp "$SUPPORT_HTML" "$SUPPORT_HTML.bak.$TIMESTAMP"
echo -e "${GREEN}✓${NC} Backup: $SUPPORT_HTML.bak.$TIMESTAMP"

# ── Step 3: Copy my-tickets.html ──
cp ./my-tickets.html "$WWWROOT/my-tickets.html"
echo -e "${GREEN}✓${NC} Deployed: $WWWROOT/my-tickets.html"

# ── Step 4: Patch support.html ──
# Add "My tickets" link to sidebar Support section.
# We look for the sidebar Support section in support.html and add the link.

python3 <<'PYEOF'
path = "/root/RunSpace/NewServer/publish/wwwroot/support.html"
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Check if already patched
if 'href="/my-tickets.html"' in content:
    print("support.html already has My tickets link")
    exit(0)

# Look for the "Contact support" nav item (current page marker with active class)
# to insert the new link after it
import re

# Common patterns that might exist in the support.html sidebar
# We look for the Support nav section and add the My tickets link

# Pattern 1: Look for an existing Support nav-section link to /support.html
pattern1 = re.compile(
    r'(<a[^>]+href="/support\.html"[^>]*class="[^"]*nav-item[^"]*active[^"]*"[^>]*>.*?</a>)',
    re.DOTALL
)

my_tickets_link = '''
      <a href="/my-tickets.html" class="nav-item">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="16" height="16"><path d="M21 8v13H3V8"/><path d="M1 3h22v5H1z"/><path d="M10 12h4"/></svg>
        My tickets
        <span class="nav-badge" id="sidebarSupportBadge" style="display:none">0</span>
      </a>'''

m = pattern1.search(content)
if m:
    # Insert after the matched support.html link
    content = content[:m.end()] + my_tickets_link + content[m.end():]
    print("Inserted My tickets link after support.html active link")
else:
    # Try another pattern: just any nav-item to /support.html
    pattern2 = re.compile(
        r'(<a[^>]+href="/support\.html"[^>]*>.*?</a>)',
        re.DOTALL
    )
    m = pattern2.search(content)
    if m:
        content = content[:m.end()] + my_tickets_link + content[m.end():]
        print("Inserted My tickets link after generic support.html link")
    else:
        print("WARNING: Could not find support.html nav link pattern — skipping sidebar insert")
        print("You may need to add the My tickets link manually.")
        exit(0)

# Ensure nav-badge CSS exists (add if not)
if '.nav-badge' not in content:
    NAV_BADGE_CSS = '''
  .nav-badge{margin-left:auto;background:#4f6ef7;color:#fff;font-size:10px;font-weight:700;min-width:18px;height:18px;border-radius:9px;display:flex;align-items:center;justify-content:center;padding:0 6px}'''
    # Insert before </style>
    if '</style>' in content:
        content = content.replace('</style>', NAV_BADGE_CSS + '\n  </style>', 1)
        print("Added .nav-badge CSS")

# Add badge-loading script
BADGE_SCRIPT = '''
<script>
(async () => {
  try {
    const res = await fetch('/api/support/unread-count', { credentials: 'include' });
    if (!res.ok) return;
    const data = await res.json();
    const badge = document.getElementById('sidebarSupportBadge');
    if (!badge) return;
    if (data.count > 0) {
      badge.textContent = data.count > 99 ? '99+' : data.count;
      badge.style.display = 'flex';
    } else {
      badge.style.display = 'none';
    }
  } catch (e) {}
})();
</script>
'''

if 'sidebarSupportBadge' in content and BADGE_SCRIPT not in content:
    # Insert before </body>
    if '</body>' in content:
        content = content.replace('</body>', BADGE_SCRIPT + '</body>', 1)
        print("Added badge-loading script")

# Also update the form success redirect if applicable
# Try to find the ticket submission redirect and make it go to my-tickets with hash
redirect_pattern = re.compile(r"window\.location\.href\s*=\s*['\"]/chatt['\"]")
if redirect_pattern.search(content):
    content = redirect_pattern.sub("window.location.href = '/my-tickets.html#' + (data.ticketId || '')", content, count=1)
    print("Updated post-submission redirect to my-tickets")

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

print("SUCCESS: support.html patched")
PYEOF

if [ $? -ne 0 ]; then
    echo -e "${RED}✗ Patch failed. Restoring backup...${NC}"
    cp "$SUPPORT_HTML.bak.$TIMESTAMP" "$SUPPORT_HTML"
    exit 1
fi

# ── Step 5: Verify ──
if grep -q 'href="/my-tickets.html"' "$SUPPORT_HTML"; then
    echo -e "${GREEN}✓${NC} support.html updated"
else
    echo -e "${YELLOW}⚠${NC} Could not verify support.html update — please check manually"
fi

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════${NC}"
echo -e "${GREEN}  Phase 2 Complete!${NC}"
echo -e "${GREEN}═══════════════════════════════════════════════${NC}"
echo ""
echo "Ready to test:"
echo "  • Open https://runspace.cloud/my-tickets.html"
echo "  • Create a ticket at https://runspace.cloud/support.html"
echo "  • Sidebar badge appears when admin replies"
echo ""
echo "Next: Phase 3 (admin-support.html inbox for mxssy/mx403)"
