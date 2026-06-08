#!/usr/bin/env python3
from pathlib import Path
import shutil

path = Path("/root/RunSpace/NewServer/publish/wwwroot/chatt.html")
backup = Path("/root/RunSpace/NewServer/publish/wwwroot/chatt.commands.bak")

if not path.exists():
    print(f"File not found: {path}")
    raise SystemExit(1)

shutil.copy2(path, backup)

text = path.read_text(encoding="utf-8")

replacements = [
    ("Rapportera användaren i denna chatt", "Report user in this chat"),
    ("Visa krypteringsstatus & enheter", "View encryption status & devices"),
    ("Radera hela konversationen lokalt", "Delete entire conversation locally"),
    ("Kör kodsnutt (kommer snart)", "Run code snippet (coming soon)"),
    ("Rensa chatthistorik (lokalt)", "Clear chat history (local)"),
    ("Stäng av ljudnotiser", "Mute notifications"),
    ("Till @", "To @"),
    ("E2E-krypterat", "E2E encrypted"),
    ("krypterat", "encrypted"),
]

for old, new in replacements:
    text = text.replace(old, new)

path.write_text(text, encoding="utf-8")

print("Commands translated to English")
print(f"Patched: {path}")
print(f"Backup:  {backup}")
