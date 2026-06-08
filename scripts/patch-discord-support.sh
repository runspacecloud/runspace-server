#!/bin/bash
# ═══════════════════════════════════════════════
# patch-discord-support.sh
# Adds Discord support to RunSpace:
#   1. Patches Python bot with HTTP API
#   2. Adds /api/support/discord-send endpoint to .NET Program.cs
#   3. Sets BOT_API_SECRET in both services
#   4. Rebuilds and restarts both services
# ═══════════════════════════════════════════════

set -e

PROGRAM_CS="/root/RunSpace/NewServer/Program.cs"
BOT_DIR="/root/RunSpace/NewServer/discord-bot"
BOT_PY="$BOT_DIR/main.py"

TIMESTAMP=$(date +%Y%m%d_%H%M%S)
PROGRAM_BACKUP="$PROGRAM_CS.bak.$TIMESTAMP"
BOT_BACKUP="$BOT_PY.bak.$TIMESTAMP"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo -e "${BLUE}  RunSpace Discord Support Patcher${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo ""

# ── Step 1: Verify files exist ──
if [ ! -f "$PROGRAM_CS" ]; then
    echo -e "${RED}✗ Error: $PROGRAM_CS not found${NC}"
    exit 1
fi
if [ ! -f "$BOT_PY" ]; then
    echo -e "${RED}✗ Error: $BOT_PY not found${NC}"
    exit 1
fi
echo -e "${GREEN}✓${NC} Found Program.cs and discord-bot/main.py"

# ── Step 2: Check if already patched ──
ALREADY_PATCHED=0
if grep -q "/api/support/discord-send" "$PROGRAM_CS"; then
    echo -e "${YELLOW}⚠ Program.cs already has discord-send endpoint${NC}"
    ALREADY_PATCHED=1
fi
if grep -q "handle_support_request" "$BOT_PY"; then
    echo -e "${YELLOW}⚠ main.py already has HTTP support handler${NC}"
    ALREADY_PATCHED=1
fi

if [ $ALREADY_PATCHED -eq 1 ]; then
    read -p "Continue anyway? (y/N) " -n 1 -r
    echo ""
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo -e "${YELLOW}Aborted.${NC}"
        exit 0
    fi
fi

# ── Step 3: Backup both files ──
cp "$PROGRAM_CS" "$PROGRAM_BACKUP"
cp "$BOT_PY" "$BOT_BACKUP"
echo -e "${GREEN}✓${NC} Backups created:"
echo "  - $PROGRAM_BACKUP"
echo "  - $BOT_BACKUP"

# ── Step 4: Patch Python bot ──
echo -e "${BLUE}→${NC} Patching Python bot..."

python3 <<'PYEOF'
import sys
import re

BOT_PY = "/root/RunSpace/NewServer/discord-bot/main.py"

with open(BOT_PY, 'r', encoding='utf-8') as f:
    content = f.read()

# ── Add import for aiohttp at the top ──
if "from aiohttp import web" not in content:
    content = re.sub(
        r"(from discord import app_commands\n)",
        r"\1from aiohttp import web\n",
        content,
        count=1
    )

# ── Add secrets import ──
if "import secrets" not in content:
    content = re.sub(
        r"(import time\n)",
        r"\1import secrets\n",
        content,
        count=1
    )

# ── Add config constants after TOKEN check ──
CONFIG_BLOCK = """
# Internal API secret — .NET backend uses this to authenticate to the bot
INTERNAL_API_SECRET = os.getenv("BOT_API_SECRET", "change-me-in-production")

# HTTP server binds to localhost only — never exposed externally
HTTP_HOST = "127.0.0.1"
HTTP_PORT = 8765

SUPPORT_RECEIVERS = [
    1474602196409651230,  # Nulligit (primary)
    1424751033162399827   # Solumverum (fallback)
]
"""

if "INTERNAL_API_SECRET" not in content:
    # Insert after the TOKEN validation block
    content = re.sub(
        r'(if not TOKEN:\s*\n\s*raise ValueError\("DISCORD_TOKEN saknas"\))',
        r'\1\n' + CONFIG_BLOCK,
        content,
        count=1
    )

# ── Hook start_http_server into setup_hook ──
if "start_http_server" not in content or "create_task(start_http_server" not in content:
    content = re.sub(
        r'(print\(f"Syncade \{len\(synced\)\} commands till guild \{GUILD_ID\}"\))',
        r'\1\n\n        # Start HTTP server for internal API\n        self.loop.create_task(start_http_server(self))',
        content,
        count=1
    )

# ── Add sanitize + handlers + start_http_server before bot.run() ──
HTTP_HANDLERS = '''

def sanitize_mentions(text: str) -> str:
    """Strip Discord @everyone/@here mentions and role pings."""
    if not text:
        return ""
    import re
    text = re.sub(r"@everyone|@here", "", text, flags=re.IGNORECASE)
    text = re.sub(r"<@[!&]?\\d+>", "", text)
    return text.strip()


async def handle_support_request(request: web.Request) -> web.Response:
    """POST /send-support — send a support DM to the support team."""

    auth_header = request.headers.get("X-Internal-Secret", "")
    if not secrets.compare_digest(auth_header, INTERNAL_API_SECRET):
        print(f"[HTTP] Unauthorized request from {request.remote}")
        return web.json_response({"error": "Unauthorized"}, status=401)

    try:
        data = await request.json()
    except Exception:
        return web.json_response({"error": "Invalid JSON"}, status=400)

    username = sanitize_mentions(str(data.get("username", "")).strip())[:50]
    category = sanitize_mentions(str(data.get("category", "")).strip())[:100]
    description = sanitize_mentions(str(data.get("description", "")).strip())[:1500]

    if not username:
        return web.json_response({"error": "Username required"}, status=400)
    if not category and not description:
        return web.json_response({"error": "Category or description required"}, status=400)

    embed = discord.Embed(
        title="\\U0001F3AB New RunSpace Support Request",
        color=discord.Color.blue(),
        timestamp=datetime.now(timezone.utc)
    )
    embed.add_field(name="Username", value=username, inline=False)
    if category:
        embed.add_field(name="Category", value=category, inline=False)
    if description:
        embed.add_field(name="Issue", value=description, inline=False)
    embed.set_footer(text="via runspace.cloud/support")

    sent_to = None
    for user_id in SUPPORT_RECEIVERS:
        try:
            user = await bot.fetch_user(user_id)
            await user.send(embed=embed)
            sent_to = user_id
            print(f"[Support] Delivered from @{username} to user {user_id}")
            break
        except discord.Forbidden:
            print(f"[Support] User {user_id} has DMs disabled, trying next")
            continue
        except Exception as e:
            print(f"[Support] Error sending to {user_id}: {e}")
            continue

    if sent_to is None:
        return web.json_response({"error": "Could not deliver message"}, status=502)

    return web.json_response({"success": True, "deliveredTo": str(sent_to)})


async def handle_health(request: web.Request) -> web.Response:
    """GET /health — simple health check."""
    return web.json_response({
        "status": "ok",
        "botReady": bot.is_ready()
    })


async def start_http_server(bot_instance):
    """Start the internal HTTP server on localhost."""
    await bot_instance.wait_until_ready()

    app = web.Application()
    app.router.add_post("/send-support", handle_support_request)
    app.router.add_get("/health", handle_health)

    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, HTTP_HOST, HTTP_PORT)
    await site.start()
    print(f"[HTTP] Internal API listening on http://{HTTP_HOST}:{HTTP_PORT}")


'''

if "async def handle_support_request" not in content:
    # Insert before bot.run(TOKEN)
    content = content.replace("bot.run(TOKEN)", HTTP_HANDLERS + "bot.run(TOKEN)", 1)

with open(BOT_PY, 'w', encoding='utf-8') as f:
    f.write(content)

# Verify
with open(BOT_PY, 'r', encoding='utf-8') as f:
    verify = f.read()

if "handle_support_request" not in verify or "INTERNAL_API_SECRET" not in verify:
    print("ERROR: Python bot patch failed verification", file=sys.stderr)
    sys.exit(1)

print("SUCCESS: Python bot patched")
PYEOF

if [ $? -ne 0 ]; then
    echo -e "${RED}✗ Python bot patch failed. Restoring...${NC}"
    cp "$BOT_BACKUP" "$BOT_PY"
    exit 1
fi
echo -e "${GREEN}✓${NC} Python bot patched"

# ── Step 5: Patch Program.cs ──
echo -e "${BLUE}→${NC} Patching Program.cs..."

python3 <<'PYEOF'
import sys

PROGRAM_CS = "/root/RunSpace/NewServer/Program.cs"

with open(PROGRAM_CS, 'r', encoding='utf-8') as f:
    content = f.read()

ENDPOINT_CODE = '''
// ═══════════════════════════════════════════════
// DISCORD SUPPORT — forwards to Python bot
// ═══════════════════════════════════════════════
app.MapPost("/api/support/discord-send", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (!limiter.IsAllowed(ip, "discord_support", 5, 3600))
        return Results.Json(new { message = "Too many requests. Please wait a while." }, statusCode: 429);

    DiscordSupportDto? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<DiscordSupportDto>();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid request body." });
    }

    if (body == null || string.IsNullOrWhiteSpace(body.Username))
        return Results.BadRequest(new { message = "Username is required." });

    var username = InputSanitizer.SanitizeInput(body.Username.Trim(), 50);
    var category = InputSanitizer.SanitizeInput(body.Category?.Trim() ?? "", 100);
    var description = InputSanitizer.SanitizeInput(body.Description?.Trim() ?? "", 1500);

    if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(description))
        return Results.BadRequest(new { message = "Category or description is required." });

    var botSecret = Environment.GetEnvironmentVariable("BOT_API_SECRET");
    if (string.IsNullOrWhiteSpace(botSecret))
    {
        Console.WriteLine("[Discord Support] BOT_API_SECRET not configured");
        return Results.Json(new { message = "Discord support is not configured." }, statusCode: 503);
    }

    var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(10);
    http.DefaultRequestHeaders.Add("X-Internal-Secret", botSecret);

    try
    {
        var payload = new { username, category, description };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await http.PostAsync("http://127.0.0.1:8765/send-support", content);

        if (resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Discord Support] Forwarded from @{username} to bot");
            AppHelpers.LogActivity(username, "discord_support_sent", "Forwarded via bot");
            return Results.Ok(new { success = true });
        }

        var errText = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"[Discord Support] Bot returned {(int)resp.StatusCode}: {errText}");
        return Results.Json(new { message = "Could not deliver message. Please try again later." }, statusCode: 502);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Discord Support] Exception contacting bot: {ex.Message}");
        return Results.Json(new { message = "Bot is not responding. Please try again later." }, statusCode: 502);
    }
});

'''

DTO_CODE = '\npublic record DiscordSupportDto(string? Username, string? Category, string? Description);\n'

# Insert endpoint before /api/admin/support/tickets
endpoint_marker = 'app.MapGet("/api/admin/support/tickets"'
if endpoint_marker not in content:
    print("ERROR: Could not find insertion point for endpoint", file=sys.stderr)
    sys.exit(1)

if "/api/support/discord-send" not in content:
    content = content.replace(endpoint_marker, ENDPOINT_CODE.strip() + '\n\n' + endpoint_marker, 1)

# Insert DTO after SupportTicketReq
dto_marker = 'public record SupportTicketReq(string? Username, string? Category, string? Subject, string? Description);'
if dto_marker not in content:
    print("ERROR: Could not find insertion point for DTO", file=sys.stderr)
    sys.exit(1)

if "DiscordSupportDto" not in content:
    content = content.replace(dto_marker, dto_marker + DTO_CODE, 1)

with open(PROGRAM_CS, 'w', encoding='utf-8') as f:
    f.write(content)

with open(PROGRAM_CS, 'r', encoding='utf-8') as f:
    verify = f.read()

if '/api/support/discord-send' not in verify or 'DiscordSupportDto' not in verify:
    print("ERROR: Program.cs patch failed verification", file=sys.stderr)
    sys.exit(1)

print("SUCCESS: Program.cs patched")
PYEOF

if [ $? -ne 0 ]; then
    echo -e "${RED}✗ Program.cs patch failed. Restoring both files...${NC}"
    cp "$PROGRAM_BACKUP" "$PROGRAM_CS"
    cp "$BOT_BACKUP" "$BOT_PY"
    exit 1
fi
echo -e "${GREEN}✓${NC} Program.cs patched"

# ── Step 6: Check/install aiohttp in bot venv ──
echo -e "${BLUE}→${NC} Checking aiohttp in bot's venv..."
if "$BOT_DIR/.venv/bin/python" -c "import aiohttp" 2>/dev/null; then
    echo -e "${GREEN}✓${NC} aiohttp already installed"
else
    echo -e "${YELLOW}→ Installing aiohttp...${NC}"
    "$BOT_DIR/.venv/bin/pip" install aiohttp
    echo -e "${GREEN}✓${NC} aiohttp installed"
fi

# ── Step 7: Generate BOT_API_SECRET if not set ──
echo -e "${BLUE}→${NC} Checking BOT_API_SECRET..."

# Check if secret is in either service
RUNSPACE_HAS_SECRET=$(sudo grep -l "BOT_API_SECRET" /etc/systemd/system/runspace.service.d/override.conf 2>/dev/null || echo "")
BOT_HAS_SECRET=$(sudo grep -l "BOT_API_SECRET" /etc/systemd/system/runspace-bot.service 2>/dev/null || echo "")

if [ -z "$RUNSPACE_HAS_SECRET" ] || [ -z "$BOT_HAS_SECRET" ]; then
    # Generate a new secret
    BOT_SECRET=$(python3 -c "import secrets; print(secrets.token_urlsafe(32))")

    echo -e "${YELLOW}→ Generating BOT_API_SECRET: $BOT_SECRET${NC}"
    echo -e "${YELLOW}  (save this — it's also written to both services)${NC}"

    # Add to runspace.service.d/override.conf
    if [ -z "$RUNSPACE_HAS_SECRET" ]; then
        sudo mkdir -p /etc/systemd/system/runspace.service.d/
        OVERRIDE_FILE="/etc/systemd/system/runspace.service.d/override.conf"
        if [ -f "$OVERRIDE_FILE" ]; then
            # Append to existing override
            echo "Environment=\"BOT_API_SECRET=$BOT_SECRET\"" | sudo tee -a "$OVERRIDE_FILE" > /dev/null
        else
            # Create new override
            sudo tee "$OVERRIDE_FILE" > /dev/null <<EOF
[Service]
Environment="BOT_API_SECRET=$BOT_SECRET"
EOF
        fi
        echo -e "${GREEN}✓${NC} Added BOT_API_SECRET to runspace.service"
    fi

    # Add to runspace-bot.service
    if [ -z "$BOT_HAS_SECRET" ]; then
        # Use sed to add after DISCORD_TOKEN line
        sudo sed -i "/Environment=DISCORD_TOKEN=/a Environment=BOT_API_SECRET=$BOT_SECRET" /etc/systemd/system/runspace-bot.service
        echo -e "${GREEN}✓${NC} Added BOT_API_SECRET to runspace-bot.service"
    fi

    sudo systemctl daemon-reload
    echo -e "${GREEN}✓${NC} systemd reloaded"
else
    echo -e "${GREEN}✓${NC} BOT_API_SECRET already configured in both services"
fi

# ── Step 8: Rebuild and restart ──
echo ""
echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo -e "${BLUE}  Rebuild and restart${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo ""
read -p "Rebuild .NET backend and restart both services now? (y/N) " -n 1 -r
echo ""

if [[ $REPLY =~ ^[Yy]$ ]]; then
    echo -e "${BLUE}→${NC} Building .NET backend..."
    cd /root/RunSpace/NewServer

    if dotnet publish -c Release -o publish; then
        echo -e "${GREEN}✓${NC} Build succeeded"

        echo -e "${BLUE}→${NC} Restarting services..."
        sudo systemctl daemon-reload
        sudo systemctl restart runspace.service
        sudo systemctl restart runspace-bot.service

        sleep 3

        RUNSPACE_OK=$(systemctl is-active runspace.service)
        BOT_OK=$(systemctl is-active runspace-bot.service)

        if [ "$RUNSPACE_OK" = "active" ] && [ "$BOT_OK" = "active" ]; then
            echo -e "${GREEN}✓${NC} Both services running"

            # Test the health endpoint
            echo -e "${BLUE}→${NC} Testing bot HTTP API..."
            sleep 2
            if curl -sf http://127.0.0.1:8765/health > /dev/null 2>&1; then
                echo -e "${GREEN}✓${NC} Bot HTTP API responding"
            else
                echo -e "${YELLOW}⚠${NC} Bot HTTP API not responding yet (may take a moment to start)"
            fi

            echo ""
            echo -e "${GREEN}═══════════════════════════════════════════════${NC}"
            echo -e "${GREEN}  Deployment complete!${NC}"
            echo -e "${GREEN}═══════════════════════════════════════════════${NC}"
            echo ""
            echo "Follow bot logs:"
            echo "  sudo journalctl -u runspace-bot.service -f"
            echo ""
            echo "Follow backend logs:"
            echo "  sudo journalctl -u runspace.service -f | grep 'Discord Support'"
            echo ""
            echo "Test bot health:"
            echo "  curl http://127.0.0.1:8765/health"
        else
            echo -e "${RED}✗ One or both services failed to start:${NC}"
            echo "  runspace.service: $RUNSPACE_OK"
            echo "  runspace-bot.service: $BOT_OK"
            echo ""
            echo "Check logs:"
            echo "  sudo journalctl -u runspace.service -n 30"
            echo "  sudo journalctl -u runspace-bot.service -n 30"
            exit 1
        fi
    else
        echo -e "${RED}✗ Build failed!${NC}"
        echo -e "${YELLOW}Restoring backups...${NC}"
        cp "$PROGRAM_BACKUP" "$PROGRAM_CS"
        cp "$BOT_BACKUP" "$BOT_PY"
        echo -e "${GREEN}✓${NC} Files restored"
        exit 1
    fi
else
    echo -e "${YELLOW}Skipped rebuild. Manual steps:${NC}"
    echo ""
    echo "    cd /root/RunSpace/NewServer"
    echo "    dotnet publish -c Release -o publish"
    echo "    sudo systemctl restart runspace.service runspace-bot.service"
    echo ""
    echo "To rollback:"
    echo "    cp $PROGRAM_BACKUP $PROGRAM_CS"
    echo "    cp $BOT_BACKUP $BOT_PY"
fi

echo ""
echo -e "${GREEN}Done!${NC}"
