import logging
from runspace import RunSpaceBot

# Setup logging so you can see what the bot does
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s"
)

# ── Configure your bot ──────────────────────────────────────
bot = RunSpaceBot(
    token="DITT_BOT_TOKEN",        # Generera i serverinställningar på RunSpace
    server_id="DITT_SERVER_ID",    # Finns i serverinställningar
    base_url="https://runspace.cloud"
)

# ── Moderation — automatiska regler ────────────────────────

# Blacklista IP-adresser direkt (laddas in vid start)
bot.ip_blacklist.add("1.2.3.4")
bot.ip_blacklist.add("5.6.7.8")

# Ordfilter — meddelanden med dessa ord tas bort automatiskt
bot.add_word_filter("spam")
bot.add_word_filter("köp nu")


# ── Event handlers ──────────────────────────────────────────

@bot.on_ready
def on_ready(server):
    print(f"✅ Bot är igång på servern: {server.name}")
    print(f"   Kanaler: {[c['name'] for c in server.channels]}")
    print(f"   Medlemmar: {len(server.members)}")


@bot.on_message
def on_message(msg):
    print(f"[{msg.channel_id}] {msg.author}: {msg.text}")

    # Kommando: !members — lista alla medlemmar
    if msg.text.strip() == "!members":
        members = bot.get_members()
        names = ", ".join(f"@{m.username}" for m in members)
        bot.send(msg.channel_id, f"👥 Medlemmar: {names}")
        return

    # Kommando: !banip <ip>
    if msg.text.startswith("!banip "):
        ip = msg.text.split(" ", 1)[1].strip()
        bot.ban_ip(ip)
        bot.send(msg.channel_id, f"🚫 IP {ip} är nu blacklistad.")
        return

    # Kommando: !kick <användare>
    if msg.text.startswith("!kick "):
        target = msg.text.split(" ", 1)[1].strip().lower()
        if bot.kick(target):
            bot.send(msg.channel_id, f"👢 @{target} kickades från servern.")
        return

    # Kommando: !help
    if msg.text.strip() == "!help":
        bot.send(msg.channel_id,
            "📖 Kommandon:\n"
            "!members — visa alla medlemmar\n"
            "!banip <ip> — blacklista en IP\n"
            "!kick <användare> — kicka en användare\n"
            "!help — visa denna lista"
        )
        return


@bot.on_member_join
def on_join(member):
    print(f"➕ {member.username} gick med i servern")
    # Hälsa nya medlemmar välkomna i första textkanalen
    bot.send("allmant", f"👋 Välkommen @{member.username}!")


@bot.on_member_leave
def on_leave(member):
    print(f"➖ {member.username} lämnade servern")


# ── Starta boten ────────────────────────────────────────────
if __name__ == "__main__":
    bot.run(poll_interval=2.0)
