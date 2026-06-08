#!/usr/bin/env python3
from pathlib import Path
import shutil

path = Path("/root/RunSpace/NewServer/publish/wwwroot/chatt.html")

if not path.exists():
    print("chatt.html hittades inte")
    exit(1)

backup = path.with_suffix(".csp.bak")
shutil.copy2(path, backup)

text = path.read_text()

# Ta bort CSP meta (frame-ancestors funkar inte i meta)
text = text.replace(
    '<meta http-equiv="Content-Security-Policy" content="default-src \'self\'; script-src \'self\' \'unsafe-inline\' https://cdn.jsdelivr.net; style-src \'self\' \'unsafe-inline\' https://fonts.googleapis.com; font-src \'self\' https://fonts.gstatic.com; img-src \'self\' data: blob: https:; media-src \'self\' blob:; connect-src \'self\' wss: https:; frame-ancestors \'none\'; base-uri \'self\'; form-action \'self\';">',
    ''
)

# Ta bort X-Frame-Options meta (ska vara header)
text = text.replace(
    '<meta http-equiv="X-Frame-Options" content="DENY">',
    ''
)

path.write_text(text)

print("Fixed CSP meta issues")
print(f"Backup saved: {backup}")
