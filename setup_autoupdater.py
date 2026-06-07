#!/usr/bin/env python3
"""
Kör detta på servern via SSH:
  ssh root@77.42.80.73
  python3 setup_autoupdater.py

Skapar:
  - /var/www/runspace/downloads/        (mapp för installer-filer)
  - /var/www/runspace/desktop-version.json
  - /root/release.sh
  - Patchar Program.cs med /api/desktop/version endpoint
  - Patchar nginx-config med /downloads/ location
"""

import os, subprocess, json

# ══════════════════════════════════════════════════════════════════════
#  KONFIG — Ändra dessa om dina sökvägar skiljer sig
# ══════════════════════════════════════════════════════════════════════

PROGRAM_CS     = None  # Auto-detect nedan
NGINX_CONF     = None  # Auto-detect nedan
DOWNLOADS_DIR  = "/var/www/runspace/downloads"
VERSION_FILE   = "/var/www/runspace/desktop-version.json"
RELEASE_SCRIPT = "/root/release.sh"

# ══════════════════════════════════════════════════════════════════════
#  AUTO-DETECT
# ══════════════════════════════════════════════════════════════════════

def find_file(name, search_dirs):
    for d in search_dirs:
        for root, dirs, files in os.walk(d):
            if name in files:
                return os.path.join(root, name)
    return None

if PROGRAM_CS is None:
    PROGRAM_CS = find_file("Program.cs", ["/var/www", "/home", "/opt", "/root"])
    if PROGRAM_CS:
        print(f"✅ Hittade Program.cs: {PROGRAM_CS}")
    else:
        print("❌ Hittade inte Program.cs — ange sökvägen manuellt i scriptet")
        PROGRAM_CS = input("Sökväg till Program.cs: ").strip()

if NGINX_CONF is None:
    for candidate in [
        "/etc/nginx/sites-available/runspace.cloud",
        "/etc/nginx/sites-available/default",
        "/etc/nginx/conf.d/runspace.conf",
        "/etc/nginx/conf.d/default.conf",
    ]:
        if os.path.isfile(candidate):
            NGINX_CONF = candidate
            break
    if NGINX_CONF:
        print(f"✅ Hittade nginx-config: {NGINX_CONF}")
    else:
        print("❌ Hittade inte nginx-config automatiskt")
        NGINX_CONF = input("Sökväg till nginx-config: ").strip()

# ══════════════════════════════════════════════════════════════════════
#  1. SKAPA MAPPAR
# ══════════════════════════════════════════════════════════════════════

print("\n── Skapar mappar ──")
os.makedirs(DOWNLOADS_DIR, exist_ok=True)
os.makedirs(os.path.dirname(VERSION_FILE), exist_ok=True)
print(f"✅ {DOWNLOADS_DIR}")

# ══════════════════════════════════════════════════════════════════════
#  2. SKAPA desktop-version.json
# ══════════════════════════════════════════════════════════════════════

print("\n── Skapar desktop-version.json ──")
version_data = {
    "version": "1.0.0",
    "downloadUrl": "https://runspace.cloud/downloads/RunSpaceSetup-1.0.0.exe",
    "changelog": "Första versionen av RunSpace Desktop",
    "mandatory": False
}
with open(VERSION_FILE, "w", encoding="utf-8") as f:
    json.dump(version_data, f, indent=2, ensure_ascii=False)
print(f"✅ {VERSION_FILE}")

# ══════════════════════════════════════════════════════════════════════
#  3. PATCHA Program.cs
# ══════════════════════════════════════════════════════════════════════

ENDPOINT_CODE = '''
// ── Auto-updater endpoint ─────────────────────────────────────────────
app.MapGet("/api/desktop/version", async () =>
{
    const string versionFile = "/var/www/runspace/desktop-version.json";
    if (!File.Exists(versionFile))
        return Results.NotFound(new { error = "No version info configured" });
    var json = await File.ReadAllTextAsync(versionFile);
    return Results.Content(json, "application/json");
});
'''

print("\n── Patchar Program.cs ──")
if os.path.isfile(PROGRAM_CS):
    with open(PROGRAM_CS, "r", encoding="utf-8") as f:
        content = f.read()

    if "/api/desktop/version" in content:
        print("⏭️  Endpoint finns redan — hoppar över")
    else:
        # Hitta app.Run() och lägg endpointen precis innan
        if "app.Run()" in content:
            content = content.replace("app.Run()", ENDPOINT_CODE + "\napp.Run()")
            with open(PROGRAM_CS, "w", encoding="utf-8") as f:
                f.write(content)
            print(f"✅ Lade till endpoint i {PROGRAM_CS}")
        else:
            print("⚠️  Hittade inte app.Run() — lägger till i slutet")
            with open(PROGRAM_CS, "a", encoding="utf-8") as f:
                f.write("\n" + ENDPOINT_CODE)
            print(f"✅ Lade till endpoint i slutet av {PROGRAM_CS}")
else:
    print(f"❌ Filen finns inte: {PROGRAM_CS}")

# ══════════════════════════════════════════════════════════════════════
#  4. PATCHA NGINX
# ══════════════════════════════════════════════════════════════════════

NGINX_SNIPPET = '''
    # ── Auto-updater: servera installer-filer ──────────────────────────
    location /downloads/ {
        alias /var/www/runspace/downloads/;
        add_header Content-Disposition "attachment";
        add_header Cache-Control "no-cache";
        types {
            application/octet-stream exe msi;
        }
    }
'''

print("\n── Patchar nginx-config ──")
if os.path.isfile(NGINX_CONF):
    with open(NGINX_CONF, "r", encoding="utf-8") as f:
        content = f.read()

    if "/downloads/" in content:
        print("⏭️  Downloads-location finns redan — hoppar över")
    else:
        # Hitta sista } i filen (slutet av server-blocket) och lägg till innan
        last_brace = content.rfind("}")
        if last_brace != -1:
            content = content[:last_brace] + NGINX_SNIPPET + "\n" + content[last_brace:]
            with open(NGINX_CONF, "w", encoding="utf-8") as f:
                f.write(content)
            print(f"✅ Lade till /downloads/ i {NGINX_CONF}")

            # Testa och ladda om nginx
            result = subprocess.run(["nginx", "-t"], capture_output=True, text=True)
            if result.returncode == 0:
                subprocess.run(["systemctl", "reload", "nginx"])
                print("✅ Nginx reloaded")
            else:
                print(f"❌ Nginx config-fel:\n{result.stderr}")
        else:
            print("❌ Kunde inte hitta server-block i nginx-config")
else:
    print(f"❌ Filen finns inte: {NGINX_CONF}")

# ══════════════════════════════════════════════════════════════════════
#  5. SKAPA release.sh
# ══════════════════════════════════════════════════════════════════════

RELEASE_SH = r'''#!/bin/bash
set -e

VERSION="$1"
CHANGELOG="$2"
MANDATORY=false
[ "$3" = "--mandatory" ] && MANDATORY=true

if [ -z "$VERSION" ] || [ -z "$CHANGELOG" ]; then
    echo "Användning: ./release.sh <version> <changelog> [--mandatory]"
    echo "Exempel:    ./release.sh 1.1.0 \"Buggfixar och förbättringar\""
    exit 1
fi

INSTALLER="RunSpaceSetup-${VERSION}.exe"
DL_DIR="/var/www/runspace/downloads"
VER_FILE="/var/www/runspace/desktop-version.json"

if [ ! -f "${DL_DIR}/${INSTALLER}" ]; then
    echo "❌ Hittade inte ${DL_DIR}/${INSTALLER}"
    echo "   Ladda upp den först:"
    echo "   scp ${INSTALLER} root@77.42.80.73:${DL_DIR}/"
    exit 1
fi

cat > "$VER_FILE" << EOF
{
  "version": "${VERSION}",
  "downloadUrl": "https://runspace.cloud/downloads/${INSTALLER}",
  "changelog": "${CHANGELOG}",
  "mandatory": ${MANDATORY}
}
EOF

echo "✅ Release ${VERSION} publicerad!"
echo "   Fil:       ${DL_DIR}/${INSTALLER}"
echo "   Mandatory: ${MANDATORY}"
echo "   Changelog: ${CHANGELOG}"
echo ""
echo "   Alla klienter ser uppdateringen vid nästa start."
'''

print("\n── Skapar release.sh ──")
with open(RELEASE_SCRIPT, "w", encoding="utf-8", newline="\n") as f:
    f.write(RELEASE_SH)
os.chmod(RELEASE_SCRIPT, 0o755)
print(f"✅ {RELEASE_SCRIPT}")

# ══════════════════════════════════════════════════════════════════════
#  KLART
# ══════════════════════════════════════════════════════════════════════

print("\n" + "═" * 60)
print("  AUTO-UPDATER SETUP KLAR!")
print("═" * 60)
print(f"""
Vad som gjordes:
  ✅ Skapade {DOWNLOADS_DIR}
  ✅ Skapade {VERSION_FILE}
  ✅ Patchade {PROGRAM_CS}
  ✅ Patchade {NGINX_CONF}
  ✅ Skapade {RELEASE_SCRIPT}

Glöm inte att restarta din .NET-app:
  systemctl restart runspace   (eller vad din service heter)

Release-flöde:
  1. Bygg installer lokalt
  2. scp RunSpaceSetup-X.X.X.exe root@77.42.80.73:{DOWNLOADS_DIR}/
  3. ssh root@77.42.80.73 "./release.sh X.X.X 'Changelog'"
""")
