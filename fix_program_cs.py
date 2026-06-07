#!/usr/bin/env python3
import re
import shutil
import sys
from pathlib import Path

def dedupe_exact_lines(text, targets):
    seen = set()
    out = []
    for line in text.splitlines(keepends=True):
        stripped = line.strip()
        if stripped in targets:
            if stripped in seen:
                continue
            seen.add(stripped)
        out.append(line)
    return "".join(out)

def collapse_forwarded_headers(text):
    pattern = re.compile(
        r'(?:^app\.UseForwardedHeaders\(new ForwardedHeadersOptions \{ .*?\}\);\n){2,}',
        re.MULTILINE
    )
    replacement = "app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });\n"
    return pattern.sub(replacement, text)

def patch_cors_headers(text):
    old = '.WithHeaders("Content-Type", "X-CSRF-Token", "X-Request-Id", "X-Device-Fingerprint")'
    new = '.WithHeaders("Content-Type", "X-CSRF-Token", "X-Request-Id", "X-Device-Fingerprint", "X-Device-Token", "X-Device-Name")'
    return text.replace(old, new)

def patch_sql(text):
    text = text.replace(
        "IpAddress= IpPrefix=$prefix",
        "IpAddress=$ip, IpPrefix=$prefix"
    )

    text = text.replace(
        "INSERT OR IGNORE INTO DeviceIpHistory (DeviceToken,  SeenAt)",
        "INSERT OR IGNORE INTO DeviceIpHistory (DeviceToken, IpAddress, SeenAt)"
    )

    text = text.replace(
        "VALUES ($tok,  datetime('now'))",
        "VALUES ($tok, $ip, datetime('now'))"
    )

    text = text.replace(
        "LastLoginIp= AccountLockedUntil=''",
        "LastLoginIp=$ip, AccountLockedUntil=''"
    )

    return text

def main():
    if len(sys.argv) != 2:
        print("Usage: python3 fix_program_cs.py Program.cs")
        return

    path = Path(sys.argv[1])
    backup = path.with_suffix(".bak")

    shutil.copy2(path, backup)

    text = path.read_text()

    text = dedupe_exact_lines(text, {
        "using Stripe;",
        "using Stripe.Checkout;"
    })

    text = collapse_forwarded_headers(text)
    text = patch_cors_headers(text)
    text = patch_sql(text)

    path.write_text(text)

    print("Fixed.")
    print(f"Backup saved as {backup}")

if __name__ == "__main__":
    main()
