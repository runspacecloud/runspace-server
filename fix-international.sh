#!/bin/bash
# === RunSpace International Access Fixes ===
# Kör alla kommandon i ordning på servern

# 1. Bunda SignalR lokalt
curl -sL "https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js" -o /root/RunSpace/NewServer/wwwroot/signalr.min.js
echo "✓ SignalR nedladdad lokalt"

# 2. Byt CDN till lokal i chatt.html
sed -i 's|https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js|/signalr.min.js|g' /root/RunSpace/NewServer/wwwroot/chatt.html
echo "✓ chatt.html uppdaterad"

# 3. Risk score threshold 80 -> 95
sed -i 's/if (riskScore >= 80)/if (riskScore >= 95)/g' /root/RunSpace/NewServer/Program.cs
echo "✓ Risk threshold höjd"

# 4. Impossible travel +40 -> +15
sed -i 's/riskScore += 40/riskScore += 15/g' /root/RunSpace/NewServer/Program.cs
echo "✓ Impossible travel sänkt"

# 5. New device +20 -> +10
sed -i 's/isNewDevice) { riskScore += 20/isNewDevice) { riskScore += 10/g' /root/RunSpace/NewServer/Program.cs
echo "✓ New device sänkt"

# 6. Travel window 5min -> 30sek
sed -i 's/TotalMinutes < 5/TotalSeconds < 30/g' /root/RunSpace/NewServer/Program.cs
echo "✓ Travel window fixad"

# 7. Session anomaly 60 -> 85
sed -i 's/AnalyzeSession(sessionInfo, currentIp, currentUa) >= 60/AnalyzeSession(sessionInfo, currentIp, currentUa) >= 85/g' /root/RunSpace/NewServer/Program.cs
echo "✓ Session anomaly relaxerad"

# 8. Inaktivitet 30min -> 2h
sed -i 's/AddMinutes(-30)/AddHours(-2)/g' /root/RunSpace/NewServer/Program.cs
echo "✓ Inaktivitetstimeout höjd"

# 9. Bygg och starta om
cd /root/RunSpace/NewServer && dotnet publish -c Release -o publish && sudo systemctl restart runspace
echo "✓ Deploy klar!"
