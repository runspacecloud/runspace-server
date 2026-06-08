from pathlib import Path

script = r'''#!/usr/bin/env python3
"""
Patchar RunSpace chatt.html för tre saker:
1. Byter STATE.connected-checks till riktig SignalR state-check
2. Gör offline-fallback robustare i send-flow
3. Fluschar offline-kön automatiskt när anslutningen kommer tillbaka

Användning:
    python3 patch_chatt_html.py /path/to/chatt.html

Skapar backup bredvid originalet som .bak
"""

from __future__ import annotations
import re
import shutil
import sys
from pathlib import Path


def patch_signalr_checks(text: str) -> str:
    helper = """  function isHubConnected() {
    return !!(STATE.conn && STATE.conn.state === 'Connected');
  }

"""
    marker = "  // ── SignalR ──\n"
    if "function isHubConnected()" not in text and marker in text:
        text = text.replace(marker, helper + marker, 1)

    replacements = {
        "if (!STATE.conn || !STATE.connected) return;": "if (!isHubConnected()) return;",
        "if (!STATE.conn || !STATE.connected) throw new Error('Not connected');": "if (!isHubConnected()) throw new Error('Not connected');",
    }
    for old, new in replacements.items():
        text = text.replace(old, new)
    return text


def patch_send_flow(text: str) -> str:
    old = """      } else if (!STATE.connected) {
        // Offline — queue
        optimistic.status = 'sending';
        optimistic.text = text || (file ? `[I kö: ${file.name}]` : '');
        // Can't queue File objects easily; skip file queue for now, inform user
        if (!file) {
          try {
            await STORE.queueAdd({ peer, text, reply: reply?.id || null });
            UI.toast('Meddelande köat offline', 'warn');
          } catch {}
        } else {
          UI.toast('Filen kunde inte köas offline', 'err');
          optimistic.failed = true;
        }
      } else {"""

    new = """      } else if (!isHubConnected() || e?.message === 'Not connected') {
        // Offline / reconnecting — queue
        optimistic.status = 'sending';
        optimistic.text = text || (file ? `[I kö: ${file.name}]` : '');
        // Can't queue File objects easily; skip file queue for now, inform user
        if (!file) {
          try {
            await STORE.queueAdd({ peer, text, reply: reply?.id || null });
            UI.toast('Meddelande köat offline', 'warn');
          } catch {}
        } else {
          UI.toast('Filen kunde inte köas offline', 'err');
          optimistic.failed = true;
          optimistic.status = 'failed';
        }
      } else {"""
    return text.replace(old, new)


def patch_reconnect_flush(text: str) -> str:
    old = """    onConn: state => {
      UI.setConn(state);
      if (state === 'connected') APP.refreshCurrent();
    },"""

    new = """    onConn: state => {
      UI.setConn(state);
      if (state === 'connected') {
        APP.refreshCurrent();
        flushQueue();
      }
    },"""
    return text.replace(old, new)


def patch_manual_flush_hook(text: str) -> str:
    old = """    window.addEventListener('online', () => {
      UI.toast('Du är online igen', 'ok');
      if (STATE.peer) flushQueue();
    });"""

    new = """    window.addEventListener('online', () => {
      UI.toast('Du är online igen', 'ok');
      flushQueue();
    });"""
    return text.replace(old, new)


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python3 patch_chatt_html.py /path/to/chatt.html")
        return 1

    path = Path(sys.argv[1])
    if not path.exists():
        print(f"File not found: {path}")
        return 1

    original = path.read_text(encoding="utf-8")
    patched = original

    patched = patch_signalr_checks(patched)
    patched = patch_send_flow(patched)
    patched = patch_reconnect_flush(patched)
    patched = patch_manual_flush_hook(patched)

    if patched == original:
        print("No changes made. Patterns may not have matched this file version.")
        return 0

    backup = path.with_suffix(path.suffix + ".bak")
    shutil.copy2(path, backup)
    path.write_text(patched, encoding="utf-8")

    print(f"Patched: {path}")
    print(f"Backup:  {backup}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
'''

out = Path('/mnt/data/patch_chatt_html.py')
out.write_text(script, encoding='utf-8')
print(f"Saved {out}")
