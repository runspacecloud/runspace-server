#!/usr/bin/env python3
from pathlib import Path
import shutil

path = Path("/root/RunSpace/NewServer/publish/wwwroot/chatt.html")
backup = Path("/root/RunSpace/NewServer/publish/wwwroot/chatt.more-english.bak")

if not path.exists():
    print(f"File not found: {path}")
    raise SystemExit(1)

shutil.copy2(path, backup)
text = path.read_text(encoding="utf-8")

replacements = [
    ('title="Rapportera"', 'title="Report"'),
    ("label: 'Rapportera'", "label: 'Report'"),
    ("`Rapportera @${STATE.peer}?\\n\\nAnledning (spam/trakasseri/hot/olagligt/annat):`",
     "`Report @${STATE.peer}?\\n\\nReason (spam/harassment/threats/illegal/other):`"),
]

for old, new in replacements:
    text = text.replace(old, new)

path.write_text(text, encoding="utf-8")

print("More chat strings translated to English")
print(f"Patched: {path}")
print(f"Backup:  {backup}")
