from pathlib import Path
import re

path = Path("chatt.html")

if not path.exists():
    raise SystemExit("Hittar inte chatt.html i denna mapp.")

html = path.read_text(encoding="utf-8")
Path("chatt.html.bak").write_text(html, encoding="utf-8")

helper = r"""
function looksEncryptedPreview(text) {
  if (!text) return false;
  return text.length > 24 && /^[A-Za-z0-9+/=_-]+$/.test(text);
}

function getConversationPreview(c) {
  var text = c.preview || c.plainText || c.decryptedText || c.text || c.message || c.Message || '';

  if (c.encrypted || c.Encrypted || looksEncryptedPreview(text)) {
    return 'Encrypted message';
  }

  return text.length > 35 ? text.substring(0, 35) + '...' : text;
}
"""

if "function getConversationPreview(c)" not in html:
    html = html.replace("function renderConvList", helper + "\nfunction renderConvList", 1)

pattern = r"var\s+text\s*=\s*c\.(text|message|Message)[^;]*;\s*[\r\n]+\s*if\s*\(\s*text\.length\s*>\s*35\s*\)\s*text\s*=\s*text\.substring\(0\s*,\s*35\)\s*\+\s*['\"]\.\.\.['\"]\s*;"

html2, count = re.subn(pattern, "var text = getConversationPreview(c);", html, count=1)

if count == 0:
    print("Hittade inte exakt preview-blocket, gör bredare fallback...")

    pattern2 = r"var\s+text\s*=\s*([^;]*c\.(?:text|message|Message)[^;]*);"
    html2, count = re.subn(pattern2, "var text = getConversationPreview(c);", html, count=1)

if count == 0:
    raise SystemExit("Kunde fortfarande inte hitta raden. Skicka output från: grep -n \"var text\\|conv-last\\|renderConvList\" chatt.html")

path.write_text(html2, encoding="utf-8")

print("Klart. Backup skapad: chatt.html.bak")
print("Ändrade preview-raden. Kör Ctrl+F5 i webbläsaren.")
