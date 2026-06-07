import os
import time
import secrets
from datetime import datetime, timezone

import discord
from discord.ext import commands
from discord import app_commands
from aiohttp import web

TOKEN = os.getenv("DISCORD_TOKEN")

if not TOKEN:
    raise ValueError("DISCORD_TOKEN saknas")

# Internal API secret — .NET backend uses this to authenticate to the bot
# Falls back to a default for dev, but should be set via env var in production
INTERNAL_API_SECRET = os.getenv("BOT_API_SECRET", "change-me-in-production")

# HTTP server binds to localhost only — never exposed externally
HTTP_HOST = "127.0.0.1"
HTTP_PORT = 8765

GUILD_ID = 1475649215026823240

REPORT_RECEIVERS = [
    1424751033162399827,  # Solumverum
    1474602196409651230   # Nulligit
]

SUPPORT_RECEIVERS = [
    1474602196409651230,  # Nulligit (primary)
    1424751033162399827   # Solumverum (fallback)
]

SUPPORT_CATEGORY_ID = 1498684541911568424
SUPPORT_CHANNEL_ID =  1501588232993902765
TICKET_STAFF_ROLE_ID = 1476233657415893086

START_TIME = datetime.now(timezone.utc)

intents = discord.Intents.default()
intents.message_content = True


class RunSpaceBot(commands.Bot):
    async def setup_hook(self):
        guild = discord.Object(id=GUILD_ID)

        self.tree.clear_commands(guild=guild)
        self.tree.copy_global_to(guild=guild)
        synced = await self.tree.sync(guild=guild)

        print(f"Syncade {len(synced)} commands till guild {GUILD_ID}")

        # Start HTTP server for internal API
        self.loop.create_task(start_http_server(self))


bot = RunSpaceBot(
    command_prefix="$",
    intents=intents
)


def format_uptime() -> str:
    delta = datetime.now(timezone.utc) - START_TIME
    total_seconds = int(delta.total_seconds())

    days, remainder = divmod(total_seconds, 86400)
    hours, remainder = divmod(remainder, 3600)
    minutes, seconds = divmod(remainder, 60)

    parts = []
    if days:
        parts.append(f"{days}d")
    if hours or days:
        parts.append(f"{hours}h")
    if minutes or hours or days:
        parts.append(f"{minutes}m")
    parts.append(f"{seconds}s")

    return " ".join(parts)


def sanitize_mentions(text: str) -> str:
    """Strip Discord @everyone/@here mentions and role pings."""
    if not text:
        return ""
    import re
    text = re.sub(r"@everyone|@here", "", text, flags=re.IGNORECASE)
    text = re.sub(r"<@[!&]?\d+>", "", text)
    return text.strip()


# HTTP API — called by .NET backend

async def handle_support_request(request: web.Request) -> web.Response:
    """POST /send-support — send a support DM to the support team."""

    # Verify internal secret
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

    # Build Discord embed
    embed = discord.Embed(
        title="🎫 New RunSpace Support Request",
        color=discord.Color.blue(),
        timestamp=datetime.now(timezone.utc)
    )
    embed.add_field(name="Username", value=username, inline=False)
    if category:
        embed.add_field(name="Category", value=category, inline=False)
    if description:
        embed.add_field(name="Issue", value=description, inline=False)
    embed.set_footer(text="via runspace.cloud/support")

    # Try sending to each support user in order
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
        return web.json_response(
            {"error": "Could not deliver message"},
            status=502
        )

    return web.json_response({"success": True, "deliveredTo": str(sent_to)})


async def handle_health(request: web.Request) -> web.Response:
    """GET /health — simple health check."""
    return web.json_response({
        "status": "ok",
        "botReady": bot.is_ready(),
        "uptime": format_uptime()
    })


async def start_http_server(bot_instance):
    """Start the internal HTTP server on localhost."""
    # Wait for bot to be ready before starting server
    await bot_instance.wait_until_ready()

    app = web.Application()
    app.router.add_post("/send-support", handle_support_request)
    app.router.add_get("/health", handle_health)

    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, HTTP_HOST, HTTP_PORT)
    await site.start()
    print(f"[HTTP] Internal API listening on http://{HTTP_HOST}:{HTTP_PORT}")


# Existing Discord features (unchanged)

class ReportModal(discord.ui.Modal, title="Report Message"):
    reported_message = discord.ui.TextInput(
        label="Vilket meddelande vill du rapportera?",
        style=discord.TextStyle.paragraph,
        placeholder="Klistra in meddelandet eller beskriv det...",
        max_length=2000
    )

    reason = discord.ui.TextInput(
        label="Varför rapporterar du?",
        style=discord.TextStyle.paragraph,
        placeholder="Beskriv varför detta rapporteras...",
        max_length=1000
    )

    async def on_submit(self, interaction: discord.Interaction):
        embed = discord.Embed(
            title="Ny rapport",
            color=discord.Color.red(),
            timestamp=datetime.now(timezone.utc)
        )

        embed.add_field(
            name="Rapporterat meddelande",
            value=str(self.reported_message),
            inline=False
        )
        embed.add_field(
            name="Anledning",
            value=str(self.reason),
            inline=False
        )
        embed.add_field(
            name="Rapporterad av",
            value=f"{interaction.user} ({interaction.user.id})",
            inline=False
        )

        if interaction.guild:
            embed.add_field(
                name="Server",
                value=f"{interaction.guild.name} ({interaction.guild.id})",
                inline=False
            )

        if interaction.channel:
            embed.add_field(
                name="Kanal",
                value=f"{interaction.channel}",
                inline=False
            )

        sent = 0

        for user_id in REPORT_RECEIVERS:
            try:
                user = await bot.fetch_user(user_id)
                await user.send(embed=embed)
                sent += 1
            except Exception as e:
                print(f"Fel vid DM till {user_id}: {e}")

        await interaction.response.send_message(
            f"Rapport skickad. ({sent}/{len(REPORT_RECEIVERS)} mottagare)",
            ephemeral=True
        )


@bot.event
async def on_ready():
    print(f"Inloggad som {bot.user} ({bot.user.id})")


@bot.tree.command(name="ping", description="Visa botens latency och svarstid")
async def ping(interaction: discord.Interaction):
    start = time.perf_counter()

    await interaction.response.send_message("Pingar...")

    response_time_ms = (time.perf_counter() - start) * 1000
    ws_latency_ms = bot.latency * 1000

    if ws_latency_ms < 100:
        color = discord.Color.green()
    elif ws_latency_ms < 250:
        color = discord.Color.gold()
    else:
        color = discord.Color.red()

    embed = discord.Embed(
        title="Pong",
        color=color,
        timestamp=datetime.now(timezone.utc)
    )

    embed.add_field(
        name="WebSocket Latency",
        value=f"{ws_latency_ms:.0f} ms",
        inline=False
    )
    embed.add_field(
        name="Response Time",
        value=f"{response_time_ms:.0f} ms",
        inline=False
    )
    embed.add_field(
        name="Uptime",
        value=format_uptime(),
        inline=False
    )

    await interaction.edit_original_response(content=None, embed=embed)


@bot.tree.command(name="help", description="Visa alla commands")
async def help_command(interaction: discord.Interaction):
    embed = discord.Embed(
        title="RunSpace Bot Commands",
        description="Tillgängliga slash commands",
        color=discord.Color.blue()
    )

    embed.add_field(name="/ping", value="Visa latency, svarstid och uptime", inline=False)
    embed.add_field(name="/report", value="Rapportera ett meddelande", inline=False)
    embed.add_field(name="/ticket", value="Create a support ticket", inline=False)
    embed.add_field(name="/userinfo", value="Visa info om en användare", inline=False)
    embed.add_field(name="/serverinfo", value="Visa info om servern", inline=False)
    embed.add_field(name="/say", value="Låt boten skriva ett meddelande", inline=False)

    await interaction.response.send_message(embed=embed, ephemeral=True)


@bot.tree.command(name="userinfo", description="Visa info om en användare")
@app_commands.describe(user="Användaren du vill se info om")
async def userinfo(interaction: discord.Interaction, user: discord.User):
    embed = discord.Embed(
        title=f"User Info - {user}",
        color=discord.Color.green()
    )

    embed.add_field(name="ID", value=str(user.id), inline=False)
    embed.add_field(
        name="Konto skapat",
        value=user.created_at.strftime("%Y-%m-%d %H:%M:%S UTC"),
        inline=False
    )
    embed.set_thumbnail(url=user.display_avatar.url)

    await interaction.response.send_message(embed=embed)


@bot.tree.command(name="serverinfo", description="Visa info om servern")
async def serverinfo(interaction: discord.Interaction):
    guild = interaction.guild

    if guild is None:
        await interaction.response.send_message(
            "Det här kommandot kan bara användas i en server.",
            ephemeral=True
        )
        return

    embed = discord.Embed(
        title=f"Server Info - {guild.name}",
        color=discord.Color.purple()
    )

    embed.add_field(name="Server ID", value=str(guild.id), inline=False)
    embed.add_field(name="Members", value=str(guild.member_count), inline=False)
    embed.add_field(name="Owner", value=str(guild.owner), inline=False)
    embed.add_field(
        name="Skapad",
        value=guild.created_at.strftime("%Y-%m-%d %H:%M:%S UTC"),
        inline=False
    )

    if guild.icon:
        embed.set_thumbnail(url=guild.icon.url)

    await interaction.response.send_message(embed=embed)


@bot.tree.command(name="say", description="Låt boten skriva ett meddelande")
@app_commands.describe(text="Texten som boten ska skriva")
async def say(interaction: discord.Interaction, text: str):
    await interaction.response.send_message(text)


@bot.tree.command(name="report", description="Rapportera ett meddelande")
async def report(interaction: discord.Interaction):
    await interaction.response.send_modal(ReportModal())



class TicketModal(discord.ui.Modal, title="Create Support Ticket"):
    subject = discord.ui.TextInput(
        label="Subject",
        placeholder="What do you need help with?",
        max_length=100
    )

    description = discord.ui.TextInput(
        label="Description",
        style=discord.TextStyle.paragraph,
        placeholder="Describe your issue...",
        max_length=1500
    )

    async def on_submit(self, interaction: discord.Interaction):
        channel = await bot.fetch_channel(SUPPORT_CHANNEL_ID)
        print(f"[Ticket] Channel loaded: {channel} ({channel.id}) type={type(channel)}")

        if channel is None:
            await interaction.response.send_message(
                "Support channel not found. Please contact staff.",
                ephemeral=True
            )
            return

        if not isinstance(channel, discord.TextChannel):
            await interaction.response.send_message(
                "Support channel is not a text channel.",
                ephemeral=True
            )
            return

        if channel.category_id != SUPPORT_CATEGORY_ID:
            print(f"[Ticket] Warning: support channel category is {channel.category_id}, expected {SUPPORT_CATEGORY_ID}")

        subject = sanitize_mentions(str(self.subject))[:100]
        description = sanitize_mentions(str(self.description))[:1500]

        embed = discord.Embed(
            title="New RunSpace Support Ticket",
            color=discord.Color.blue(),
            timestamp=datetime.now(timezone.utc)
        )

        embed.add_field(
            name="Subject",
            value=subject or "No subject",
            inline=False
        )

        embed.add_field(
            name="Description",
            value=description or "No description",
            inline=False
        )

        if interaction.channel:
            embed.add_field(
                name="Created from",
                value=f"{interaction.channel.mention}",
                inline=False
            )

        embed.set_footer(text="RunSpace Discord Support")

        staff_role = interaction.guild.get_role(TICKET_STAFF_ROLE_ID) if interaction.guild else None

        ticket_id = secrets.token_hex(4)
        thread_name = f"ticket-{ticket_id}"

        thread = await channel.create_thread(
            name=thread_name[:100],
            type=discord.ChannelType.private_thread,
            invitable=False,
            reason=f"Support ticket created by {interaction.user}"
        )

        await thread.add_user(interaction.user)

        await thread.send(
            content="New private support ticket created.",
            embed=embed
        )

        await interaction.response.send_message(
            f"Your private support ticket has been created: {thread.mention}\nTicket owner: {interaction.user.mention}\nTicket ID: `{ticket_id}`",
            ephemeral=True
        )


@bot.tree.command(name="ticket", description="Create a support ticket")
async def ticket(interaction: discord.Interaction):
    await interaction.response.send_modal(TicketModal())



@bot.command(name="clear")
@commands.has_permissions(manage_messages=True)
async def clear(ctx, amount: int = 10):
    if amount < 1:
        await ctx.send("Use: `$clear 1-100`", delete_after=5)
        return

    amount = min(amount, 100)
    deleted = await ctx.channel.purge(limit=amount + 1)
    await ctx.send(f"Deleted {len(deleted) - 1} messages.", delete_after=5)


@clear.error
async def clear_error(ctx, error):
    if isinstance(error, commands.MissingPermissions):
        await ctx.send("You need Manage Messages permission.", delete_after=5)
    elif isinstance(error, commands.MissingRequiredArgument):
        await ctx.send("Use: `$clear 10`", delete_after=5)
    else:
        await ctx.send("Could not clear messages.", delete_after=5)
        print(f"[Clear] Error: {error}")

bot.run(TOKEN)
