import json
import csv
import os
import sqlite3
import sys
import uuid
import secrets
import hashlib
from datetime import datetime, timezone
from getpass import getpass
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

DB_PATH_DEFAULT = "/root/RunSpace/data/runspace.db"
OWNER_USERNAME_DEFAULT = "mx403"
SYSTEM_SENDER_DEFAULT = "mx403"


class RunSpaceOwnerCLI:
    def __init__(self):
        self.db_path = DB_PATH_DEFAULT
        self.owner_username = OWNER_USERNAME_DEFAULT
        self.system_sender = SYSTEM_SENDER_DEFAULT

    # ---------------------------
    # Database helpers
    # ---------------------------
    def connect(self):
        if not self.db_path.strip():
            raise RuntimeError("DB path saknas.")
        if not os.path.exists(self.db_path):
            raise RuntimeError(f"Databasen hittades inte: {self.db_path}")

        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys = ON")
        conn.execute("PRAGMA busy_timeout = 5000")
        return conn

    def query_all(self, sql, params=()):
        with self.connect() as conn:
            return conn.execute(sql, params).fetchall()

    def query_one(self, sql, params=()):
        with self.connect() as conn:
            return conn.execute(sql, params).fetchone()

    def execute(self, sql, params=()):
        with self.connect() as conn:
            cur = conn.execute(sql, params)
            conn.commit()
            return cur.rowcount

    def log_activity(self, action: str, details: str, username: str | None = None):
        ts = datetime.now(timezone.utc).isoformat()
        actor = (username or self.owner_username or OWNER_USERNAME_DEFAULT).strip().lower()
        self.execute(
            "INSERT INTO ActivityLog (Username, Action, Details, Timestamp) VALUES (?, ?, ?, ?)",
            (actor, action, f"[owner-cli] {details}", ts),
        )

    # ---------------------------
    # Formatting helpers
    # ---------------------------
    def print_header(self, title: str):
        line = "=" * 78
        print("" + line + "" + title + "" + line)

    def prompt(self, text: str, default: str | None = None, allow_empty: bool = False):
        suffix = f" [{default}]" if default not in (None, "") else ""
        while True:
            value = input(f"{text}{suffix}: ").strip()
            if value:
                return value
            if default is not None:
                return default
            if allow_empty:
                return ""
            print("Värde krävs.")

    def prompt_multiline(self, title: str):
        print(f"{title} (avsluta med en ensam punkt på egen rad)")
        lines = []
        while True:
            line = input()
            if line == ".":
                break
            lines.append(line)
        return "\n".join(lines).strip()

    def confirm(self, text: str):
        value = input(f"{text} [y/N]: ").strip().lower()
        return value in {"y", "yes", "j", "ja"}

    def short_dt(self, value):
        if not value:
            return "-"
        try:
            dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
            return dt.strftime("%Y-%m-%d %H:%M:%S")
        except Exception:
            return str(value)

    def parse_badges(self, raw):
        if not raw:
            return []
        try:
            parsed = json.loads(raw)
            if isinstance(parsed, list):
                return [str(x).strip().lower() for x in parsed if str(x).strip()]
        except Exception:
            pass
        return []

    def print_rows(self, headers, rows):
        headers = list(headers)
        processed = []
        for row in rows:
            processed.append(["" if v is None else str(v) for v in row])

        widths = [len(h) for h in headers]
        for row in processed:
            for i, value in enumerate(row):
                widths[i] = min(max(widths[i], len(value)), 60)

        def clip(value, width):
            return value if len(value) <= width else value[: width - 3] + "..."

        print(" | ".join(headers[i].ljust(widths[i]) for i in range(len(headers))))
        print("-+-".join("-" * widths[i] for i in range(len(headers))))
        for row in processed:
            print(" | ".join(clip(row[i], widths[i]).ljust(widths[i]) for i in range(len(headers))))

    # ---------------------------
    # Overview
    # ---------------------------
    def show_overview(self):
        self.print_header("RunSpace Owner CLI - Overview")
        users = self.query_one("SELECT COUNT(*) AS c FROM AuthUsers")["c"]
        banned = self.query_one("SELECT COUNT(*) AS c FROM AuthUsers WHERE lower(Status)='banned'")["c"]
        messages = self.query_one("SELECT COUNT(*) AS c FROM ChatMessages")["c"]
        open_tickets = self.query_one("SELECT COUNT(*) AS c FROM SupportTickets WHERE lower(Status)='open'")["c"]
        progress_tickets = self.query_one("SELECT COUNT(*) AS c FROM SupportTickets WHERE lower(Status)='in_progress'")["c"]
        closed_tickets = self.query_one("SELECT COUNT(*) AS c FROM SupportTickets WHERE lower(Status)='closed'")["c"]

        print(f"Database      : {self.db_path}")
        print(f"Owner         : {self.owner_username}")
        print(f"System sender : {self.system_sender}")
        print(f"Users         : {users}")
        print(f"Banned        : {banned}")
        print(f"Messages      : {messages}")
        print(f"Open tickets  : {open_tickets}")
        print(f"In progress   : {progress_tickets}")
        print(f"Closed tickets: {closed_tickets}")
        print()
        print("Noteringar:")
        print("- Latest login hämtas från ActivityLog eftersom AuthUsers inte har egen last-login kolumn.")
        print("- 2FA finns inte i Program.cs du skickade, så den visas som unavailable.")
        print("- Roller finns inte som egen kolumn; badges används som närmaste ersättning.")
        print("- System messages skickas som vanliga ChatMessages från valt systemkonto.")

    # ---------------------------
    # Users
    # ---------------------------
    def list_users(self, query: str | None = None, limit: int | None = None):
        sql = """
            SELECT u.Username, u.Status, u.Badges, u.CreatedAt,
                   (
                       SELECT a.Timestamp
                       FROM ActivityLog a
                       WHERE lower(a.Username)=lower(u.Username)
                         AND lower(a.Action)='login'
                       ORDER BY a.Id DESC
                       LIMIT 1
                   ) AS LatestLogin,
                   u.Bio
            FROM AuthUsers u
        """
        params = []
        if query:
            sql += " WHERE lower(u.Username) LIKE ?"
            params.append(f"%{query.lower()}%")
        sql += " ORDER BY u.Username ASC"
        if limit is not None:
            sql += " LIMIT ?"
            params.append(limit)

        rows = self.query_all(sql, params)
        printable = []
        for row in rows:
            printable.append([
                row["Username"],
                row["Status"] or "verified",
                ", ".join(self.parse_badges(row["Badges"])),
                self.short_dt(row["CreatedAt"]),
                self.short_dt(row["LatestLogin"]),
                (row["Bio"] or "").replace("", " "),])

        self.print_header("Users")
        self.print_rows(["Username", "Status", "Badges", "Created", "Latest login", "Bio"], printable)
        print(f"Visar {len(rows)} användare.")

    def select_user(self):
        username = self.prompt("Användarnamn").strip().lower()
        row = self.query_one(
            """
            SELECT u.Username, u.Bio, u.AvatarUrl, u.CreatedAt, u.Status, u.Badges,
                   (
                       SELECT a.Timestamp
                       FROM ActivityLog a
                       WHERE lower(a.Username)=lower(u.Username)
                         AND lower(a.Action)='login'
                       ORDER BY a.Id DESC
                       LIMIT 1
                   ) AS LatestLogin
            FROM AuthUsers u
            WHERE lower(u.Username)=?
            LIMIT 1
            """,
            (username,),
        )
        if not row:
            print("Användaren hittades inte.")
            return None
        return row

    def show_user_details(self):
        row = self.select_user()
        if not row:
            return
        self.print_header(f"User: {row['Username']}")
        print(f"Username     : {row['Username']}")
        print(f"Status       : {row['Status'] or 'verified'}")
        print(f"Badges       : {', '.join(self.parse_badges(row['Badges'])) or '-'}")
        print(f"CreatedAt    : {self.short_dt(row['CreatedAt'])}")
        print(f"Latest login : {self.short_dt(row['LatestLogin'])}")
        print("2FA status   : unavailable in current backend")
        print(f"AvatarUrl    : {row['AvatarUrl'] or '-'}")
        print("Bio:")
        print(row["Bio"] or "-")

    def rename_user(self):
        row = self.select_user()
        if not row:
            return
        old_username = row["Username"].strip().lower()
        new_username = self.prompt("Nytt användarnamn").strip().lower()
        if len(new_username) < 3:
            print("Användarnamnet är för kort.")
            return

        existing = self.query_one("SELECT 1 FROM AuthUsers WHERE lower(Username)=lower(?)", (new_username,))
        if existing:
            print("Det användarnamnet finns redan.")
            return

        if not self.confirm(f"Byt namn från {old_username} till {new_username}?"):
            return

        with self.connect() as conn:
            conn.execute("UPDATE AuthUsers SET Username=? WHERE lower(Username)=lower(?)", (new_username, old_username))
            conn.execute("UPDATE UserDeviceKeys SET Username=? WHERE lower(Username)=lower(?)", (new_username, old_username))
            conn.execute("UPDATE ChatMessages SET FromUser=? WHERE lower(FromUser)=lower(?)", (new_username, old_username))
            conn.execute("UPDATE ChatMessages SET ToUser=? WHERE lower(ToUser)=lower(?)", (new_username, old_username))
            conn.execute("UPDATE SupportTickets SET Username=? WHERE lower(Username)=lower(?)", (new_username, old_username))
            conn.execute("UPDATE ActivityLog SET Username=? WHERE lower(Username)=lower(?)", (new_username, old_username))
            conn.commit()

        self.log_activity("owner_rename_user", f"Renamed user {old_username} -> {new_username}")
        print("Klart.")

    def edit_bio(self):
        row = self.select_user()
        if not row:
            return
        print("Nuvarande bio:")
        print(row["Bio"] or "-")
        print()
        new_bio = self.prompt_multiline("Ny bio")
        if len(new_bio) > 500:
            print("Bio får max vara 500 tecken.")
            return
        self.execute("UPDATE AuthUsers SET Bio=? WHERE lower(Username)=lower(?)", (new_bio, row["Username"]))
        self.log_activity("owner_edit_bio", f"Updated bio for {row['Username']}")
        print("Bio uppdaterad.")

    def set_avatar_url(self):
        row = self.select_user()
        if not row:
            return
        print(f"Nuvarande avatar: {row['AvatarUrl'] or '-'}")
        value = self.prompt("Ny AvatarUrl", allow_empty=True)
        self.execute("UPDATE AuthUsers SET AvatarUrl=? WHERE lower(Username)=lower(?)", (value.strip(), row["Username"]))
        self.log_activity("owner_set_avatar", f"Updated avatar URL for {row['Username']}")
        print("AvatarUrl uppdaterad.")

    def ban_user(self):
        row = self.select_user()
        if not row:
            return
        if not self.confirm(f"Banna {row['Username']}?"):
            return
        self.execute("UPDATE AuthUsers SET Status='banned' WHERE lower(Username)=lower(?)", (row["Username"],))
        self.log_activity("owner_ban_user", f"Banned user {row['Username']}")
        print("Användaren är bannad.")

    def unban_user(self):
        row = self.select_user()
        if not row:
            return
        self.execute("UPDATE AuthUsers SET Status='verified' WHERE lower(Username)=lower(?)", (row["Username"],))
        self.log_activity("owner_unban_user", f"Unbanned user {row['Username']}")
        print("Användaren är unbannad och satt till verified.")

    def set_verified(self):
        row = self.select_user()
        if not row:
            return
        badges = self.parse_badges(row["Badges"])
        if "verified" not in badges:
            badges.append("verified")
        badges = sorted(set(badges))
        self.execute(
            "UPDATE AuthUsers SET Status='verified', Badges=? WHERE lower(Username)=lower(?)",
            (json.dumps(badges), row["Username"]),
        )
        self.log_activity("owner_set_verified", f"Set verified for {row['Username']}")
        print("Verified satt.")

    def edit_badges(self):
        row = self.select_user()
        if not row:
            return
        current = ", ".join(self.parse_badges(row["Badges"]))
        print(f"Nuvarande badges: {current or '-'}")
        raw = self.prompt("Nya badges, kommaseparerade", allow_empty=True)
        badges = sorted(set([x.strip().lower() for x in raw.split(",") if x.strip()]))
        self.execute("UPDATE AuthUsers SET Badges=? WHERE lower(Username)=lower(?)", (json.dumps(badges), row["Username"]))
        self.log_activity("owner_edit_badges", f"Updated badges for {row['Username']}: {badges}")
        print("Badges uppdaterade.")



    def bulk_add_badge(self):
        self.print_header("Mass add badge")

        badge = self.prompt("Badge").strip().lower()

        print("1. Exakt användarnamn")
        print("2. Flera användarnamn")
        print("3. Filter")
        print("0. Avbryt")

        mode = self.prompt("Val")

        users = []

        if mode == "1":
            username = self.prompt("Username").strip().lower()

            users = self.query_all(
                """
                SELECT Username, Badges
                FROM AuthUsers
                WHERE lower(Username)=?
                """,
                (username,)
            )


        elif mode == "2":
            raw = self.prompt(
                "Usernames (komma-separerade)"
            )

            names = [
                x.strip().lower()
                for x in raw.split(",")
                if x.strip()
            ]

            if not names:
                return

            placeholders = ",".join(
                "?"
                for _ in names
            )

            users = self.query_all(
                f"""
                SELECT Username, Badges
                FROM AuthUsers
                WHERE lower(Username)
                IN ({placeholders})
                """,
                names
            )


        elif mode == "3":

            print("1. Alla")
            print("2. Verified")
            print("3. Banned")

            choice = self.prompt("Val")

            where_sql = "1=1"

            if choice == "2":
                where_sql = (
                    "lower(Status)='verified'"
                )

            elif choice == "3":
                where_sql = (
                    "lower(Status)='banned'"
                )

            users = self.query_all(
                f"""
                SELECT Username, Badges
                FROM AuthUsers
                WHERE {where_sql}
                """
            )

        else:
            return


        if not users:
            print("Inga matchningar.")
            return


        print(
            f"Matchade användare: {len(users)}"
        )

        self.print_rows(
            ["Username"],
            [[u["Username"]] for u in users]
        )


        if not self.confirm(
            f"Lägg till '{badge}'?"
        ):
            return


        updated = 0

        with self.connect() as conn:

            for user in users:

                badges = self.parse_badges(
                    user["Badges"]
                )

                if badge not in badges:

                    badges.append(
                        badge
                    )

                    conn.execute(
                        """
                        UPDATE AuthUsers
                        SET Badges=?
                        WHERE lower(Username)=lower(?)
                        """,
                        (
                            json.dumps(
                                sorted(
                                    set(badges)
                                )
                            ),
                            user["Username"]
                        )
                    )

                    updated += 1

            conn.commit()


        self.log_activity(
            "owner_bulk_add_badge",
            f"Added {badge} to {updated}"
        )

        print(
            f"Klart. {updated} uppdaterade."
        )
    def bulk_remove_badge(self):
        self.print_header("Mass remove badge")
        badge = self.prompt("Badge att ta bort").strip().lower()

        if not badge:
            print("Badge krävs.")
            return

        users = self.query_all(
            "SELECT Username, Badges FROM AuthUsers WHERE Badges IS NOT NULL AND Badges != ''"
        )

        matched = []
        for user in users:
            badges = self.parse_badges(user["Badges"])
            if badge in badges:
                matched.append(user)

        print(f"Matchade användare: {len(matched)}")
        if not matched:
            return

        preview = [[u["Username"], ", ".join(self.parse_badges(u["Badges"]))] for u in matched[:100]]
        self.print_rows(["Username", "Badges"], preview)

        if not self.confirm(f"Ta bort badge '{badge}' från alla matchade användare?"):
            return

        updated = 0
        with self.connect() as conn:
            for user in matched:
                badges = self.parse_badges(user["Badges"])
                if badge in badges:
                    badges.remove(badge)
                    conn.execute(
                        "UPDATE AuthUsers SET Badges=? WHERE lower(Username)=lower(?)",
                        (json.dumps(sorted(set(badges))), user["Username"])
                    )
                    updated += 1
            conn.commit()

        self.log_activity("owner_bulk_remove_badge", f"Removed badge '{badge}' from {updated} users")
        print(f"Klart. Uppdaterade {updated} användare.")

    def delete_user(self):
        row = self.select_user()
        if not row:
            return
        username = row["Username"].strip().lower()
        typed = self.prompt(f"Skriv exakt '{username}' för att radera användaren", allow_empty=True)
        if typed != username:
            print("Avbrutet.")
            return

        with self.connect() as conn:
            conn.execute("DELETE FROM UserDeviceKeys WHERE lower(Username)=lower(?)", (username,))
            conn.execute("DELETE FROM SupportTickets WHERE lower(Username)=lower(?)", (username,))
            conn.execute("DELETE FROM ChatMessages WHERE lower(FromUser)=lower(?) OR lower(ToUser)=lower(?)", (username, username))
            conn.execute("DELETE FROM ActivityLog WHERE lower(Username)=lower(?)", (username,))
            conn.execute("DELETE FROM AuthUsers WHERE lower(Username)=lower(?)", (username,))
            conn.commit()

        self.log_activity("owner_delete_user", f"Deleted user {username}")
        print("Användaren är raderad.")

    def bulk_delete_users(self):
        self.print_header("Massradera användare")
        print("1. Bara numeriska användarnamn")
        print("2. Max antal tecken")
        print("3. Exakt antal tecken")
        print("4. Bara bokstäver")
        print("5. Bara bokstäver och siffror")
        print("6. Användarnamn som börjar med prefix")
        print("7. Användarnamn som innehåller text")
        print("0. Avbryt")

        choice = self.prompt("Val")
        if choice == "0":
            return

        where_sql = ""
        params = []
        description = ""

        if choice == "1":
            where_sql = "Username GLOB '[0-9]*' AND Username NOT GLOB '*[^0-9]*'"
            description = "bara numeriska användarnamn"
        elif choice == "2":
            max_len = self.prompt("Max antal tecken")
            try:
                max_len_i = int(max_len)
            except ValueError:
                print("Ogiltigt nummer.")
                return
            where_sql = "length(Username) <= ?"
            params = [max_len_i]
            description = f"användarnamn med max {max_len_i} tecken"
        elif choice == "3":
            exact_len = self.prompt("Exakt antal tecken")
            try:
                exact_len_i = int(exact_len)
            except ValueError:
                print("Ogiltigt nummer.")
                return
            where_sql = "length(Username) = ?"
            params = [exact_len_i]
            description = f"användarnamn med exakt {exact_len_i} tecken"
        elif choice == "4":
            where_sql = "Username GLOB '[A-Za-z]*' AND Username NOT GLOB '*[^A-Za-z]*'"
            description = "användarnamn med bara bokstäver"
        elif choice == "5":
            where_sql = "Username GLOB '[A-Za-z0-9]*' AND Username NOT GLOB '*[^A-Za-z0-9]*'"
            description = "användarnamn med bara bokstäver och siffror"
        elif choice == "6":
            prefix = self.prompt("Prefix").strip().lower()
            where_sql = "lower(Username) LIKE ?"
            params = [prefix + "%"]
            description = f"användarnamn som börjar med '{prefix}'"
        elif choice == "7":
            text = self.prompt("Text som ska finnas i användarnamn").strip().lower()
            where_sql = "lower(Username) LIKE ?"
            params = ["%" + text + "%"]
            description = f"användarnamn som innehåller '{text}'"
        else:
            print("Ogiltigt val.")
            return

        preview_sql = f"SELECT Username, Status, CreatedAt FROM AuthUsers WHERE {where_sql} ORDER BY Username ASC LIMIT 200"
        rows = self.query_all(preview_sql, params)

        if not rows:
            print("Inga användare matchade filtret.")
            return

        self.print_header(f"Förhandsvisning - {description}")
        self.print_rows(
            ["Username", "Status", "CreatedAt"],
            [[r["Username"], r["Status"], self.short_dt(r["CreatedAt"])] for r in rows]
        )

        total_row = self.query_one(f"SELECT COUNT(*) AS c FROM AuthUsers WHERE {where_sql}", params)
        total_count = total_row["c"] if total_row else 0
        print(f"\nTotalt matchande användare: {total_count}")
        print("OBS: detta raderar även deras device keys, tickets, chatmeddelanden och activity logs.")

        typed = self.prompt("Skriv DELETE för att fortsätta", allow_empty=True)
        if typed != "DELETE":
            print("Avbrutet.")
            return

        matched_users = self.query_all(f"SELECT Username FROM AuthUsers WHERE {where_sql}", params)
        usernames = [r["Username"].strip().lower() for r in matched_users]
        if not usernames:
            print("Inget att radera.")
            return

        with self.connect() as conn:
            for username in usernames:
                conn.execute("DELETE FROM UserDeviceKeys WHERE lower(Username)=lower(?)", (username,))
                conn.execute("DELETE FROM SupportTickets WHERE lower(Username)=lower(?)", (username,))
                conn.execute("DELETE FROM ChatMessages WHERE lower(FromUser)=lower(?) OR lower(ToUser)=lower(?)", (username, username))
                conn.execute("DELETE FROM ActivityLog WHERE lower(Username)=lower(?)", (username,))
                conn.execute("DELETE FROM AuthUsers WHERE lower(Username)=lower(?)", (username,))
            conn.commit()

        self.log_activity("owner_bulk_delete_users", f"Deleted {len(usernames)} users matching filter: {description}")
        print(f"Klart. Raderade {len(usernames)} användare.")

    def bulk_delete_suspicious_usernames(self):
        self.print_header("Radera misstänkta användarnamn")
        print("Det här försöker hitta bottar, scamkonton, sexuella namn, malware-/hack-ord och annan skräpnamngivning.")
        print("Det kan ge falska träffar, så förhandsgranska alltid listan först.")

        suspicious_terms = [
            # scam / fraud / impersonation
            "scam", "scammer", "fraud", "fake", "support", "helpdesk", "recovery", "recover", "refund", "cashapp", "paypal", "binance", "wallet", "crypto", "btc", "eth", "trading", "giftcard", "airdrop", "bonus", "prize", "winner", "claim", "verification", "verify", "official", "admin", "moderator", "staff",
            # malware / abuse / attack / infra
            "virus", "malware", "trojan", "stealer", "logger", "keylog", "rat", "botnet", "ddos", "dos", "phish", "phishing", "inject", "exploit", "payload", "shell", "backdoor", "crack", "cheat", "spam", "spammer", "spoof", "grabber", "cookie", "token", "breach", "leak", "raid", "raider", "nuke", "nuker", ".onion", "onion", "tor", "darkweb",
            # porn / sexual / explicit
            "porn", "sex", "sexy", "nsfw", "nude", "nudes", "horny", "blowjob", "blow", "bj", "deepthroat", "cum", "cumming", "anal", "ass", "assgape", "asshole", "bitch", "bitchslap", "slut", "whore", "milf", "dick", "cock", "penis", "vagina", "pussy", "boobs", "tits", "fetish", "rape", "rapist", "pedo", "pedo", "pedophile", "loli", "incest",
            # hate / violent / extremist / disturbing
            "auschwitz", "hitler", "nazi", "terror", "terrorist", "isis", "kkk", "genocide", "cannibal", "murder", "killer", "suicide", "selfharm", "gore", "blood", "deadbody",
            # obvious junk / throwaway / bot naming
            "account", "test", "temp", "tmp", "guest", "throwaway", "burner", "bot", "autogen", "generated", "random", "newuser", "unknown", "null", "none", "void", "useruser", "aaaa", "qwerty", "asdf", "zxcv", "0000", "1111", "1234", "12345", "123456", "abcdef"
        ]

        print("1. Endast blacklist-ord")
        print("2. Aggressiv: blacklist + väldigt korta namn + bara siffror")
        print("3. Anpassa själv (redigera termer manuellt i kod senare)")
        print("0. Avbryt")
        mode = self.prompt("Val", default="1")
        if mode == "0":
            return

        conditions = []
        params = []

        for term in sorted(set(suspicious_terms)):
            conditions.append("lower(Username) LIKE ?")
            params.append(f"%{term.lower()}%")

        description_parts = ["blacklist-ord"]

        if mode == "2":
            conditions.append("length(Username) <= 3")
            conditions.append("Username GLOB '[0-9]*' AND Username NOT GLOB '*[^0-9]*'")
            description_parts.append("max 3 tecken")
            description_parts.append("bara siffror")

        where_sql = " OR ".join(f"({c})" for c in conditions)
        description = ", ".join(description_parts)

        preview_sql = f"SELECT Username, Status, CreatedAt FROM AuthUsers WHERE {where_sql} ORDER BY Username ASC LIMIT 300"
        rows = self.query_all(preview_sql, params)
        if not rows:
            print("Inga användare matchade filtret.")
            return

        self.print_header(f"Förhandsvisning - {description}")
        self.print_rows(
            ["Username", "Status", "CreatedAt"],
            [[r["Username"], r["Status"], self.short_dt(r["CreatedAt"])] for r in rows]
        )

        total_row = self.query_one(f"SELECT COUNT(*) AS c FROM AuthUsers WHERE {where_sql}", params)
        total_count = total_row["c"] if total_row else 0
        print(f"\nTotalt matchande användare: {total_count}")
        print("OBS: detta kan ge falska träffar. Kontrollera listan noga först.")
        print("Detta raderar även deras device keys, tickets, chatmeddelanden och activity logs.")

        typed = self.prompt("Skriv DELETE för att fortsätta", allow_empty=True)
        if typed != "DELETE":
            print("Avbrutet.")
            return

        matched_users = self.query_all(f"SELECT Username FROM AuthUsers WHERE {where_sql}", params)
        usernames = [r["Username"].strip().lower() for r in matched_users]
        if not usernames:
            print("Inget att radera.")
            return

        with self.connect() as conn:
            for username in usernames:
                conn.execute("DELETE FROM UserDeviceKeys WHERE lower(Username)=lower(?)", (username,))
                conn.execute("DELETE FROM SupportTickets WHERE lower(Username)=lower(?)", (username,))
                conn.execute("DELETE FROM ChatMessages WHERE lower(FromUser)=lower(?) OR lower(ToUser)=lower(?)", (username, username))
                conn.execute("DELETE FROM ActivityLog WHERE lower(Username)=lower(?)", (username,))
                conn.execute("DELETE FROM AuthUsers WHERE lower(Username)=lower(?)", (username,))
            conn.commit()

        self.log_activity("owner_bulk_delete_suspicious", f"Deleted {len(usernames)} users matching suspicious filter: {description}")
        print(f"Klart. Raderade {len(usernames)} användare.")



    def generate_account_key_for_user(self):
        self.print_header("Account Key Wizard")

        print("Det här skapar en ny .key-fil och sparar ny AccountKey i databasen.")
        print("Användaren kan logga in med .key-filen + passphrase.")
        print()
        print("VIKTIGT:")
        print("- Den gamla account keyn ersätts.")
        print("- Om du väljer passphrase åt användaren kan du tekniskt logga in.")
        print("- För riktig privacy bör användaren senare generera sin egen nyckel.")
        print()

        username = self.prompt("Username").strip().lower()

        if "/" in username or "\\" in username or username.endswith(".key"):
            print()
            print("FEL: Skriv bara användarnamn, inte filväg.")
            print("Rätt exempel: x43")
            print("Fel exempel : /root/RunSpace/NewServer/x43.key")
            return

        row = self.query_one(
            """
            SELECT Username, Status, Badges, CreatedAt, AccountKey
            FROM AuthUsers
            WHERE lower(Username)=lower(?)
            LIMIT 1
            """,
            (username,),
        )

        if not row:
            print()
            print(f"Användaren '{username}' hittades inte.")
            return

        print()
        self.print_rows(
            ["Username", "Status", "Badges", "Created", "Has key"],
            [[
                row["Username"],
                row["Status"] or "verified",
                ", ".join(self.parse_badges(row["Badges"])) or "-",
                self.short_dt(row["CreatedAt"]),
                "yes" if row["AccountKey"] else "no",
            ]]
        )

        if row["AccountKey"]:
            print()
            print("Den här användaren har redan en AccountKey.")
            if not self.confirm("Vill du ersätta den med en ny?"):
                print("Avbrutet.")
                return

        print()
        print("Passphrase:")
        print("1. Skriv egen passphrase")
        print("2. Auto-generera stark passphrase")
        print("0. Avbryt")

        mode = self.prompt("Val", default="2").strip()

        if mode == "0":
            print("Avbrutet.")
            return

        if mode == "2":
            import secrets
            import string

            alphabet = string.ascii_letters + string.digits + "-_!?"
            passphrase = "".join(secrets.choice(alphabet) for _ in range(24))

            print()
            print("Auto-genererad passphrase:")
            print(passphrase)
            print()
            print("Spara den här säkert och skicka separat från .key-filen.")

            if not self.confirm("Fortsätt med denna passphrase?"):
                print("Avbrutet.")
                return

        else:
            passphrase = getpass("Passphrase för .key-filen: ").strip()
            if not passphrase:
                print("Passphrase krävs.")
                return

            if len(passphrase) < 8:
                print("Passphrase är väldigt kort. Använd minst 8 tecken.")
                if not self.confirm("Fortsätt ändå?"):
                    print("Avbrutet.")
                    return

            confirm_passphrase = getpass("Bekräfta passphrase: ").strip()
            if passphrase != confirm_passphrase:
                print("Passphrase matchar inte.")
                return

        output_dir = self.prompt(
            "Output folder",
            default="/root/RunSpace/NewServer"
        ).strip()

        if not os.path.isdir(output_dir):
            print(f"Mappen finns inte: {output_dir}")
            return

        filepath = os.path.join(output_dir, f"{username}.key")

        print()
        print("Preview:")
        print(f"Username : {username}")
        print(f"Key file : {filepath}")
        print("Database : AuthUsers.AccountKey kommer uppdateras")
        print()

        if not self.confirm("Skapa ny key och uppdatera databasen?"):
            print("Avbrutet.")
            return

        account_key = str(uuid.uuid4())

        header = b"RSK1"
        salt = os.urandom(16)
        iv = os.urandom(12)

        kdf = PBKDF2HMAC(
            algorithm=hashes.SHA256(),
            length=32,
            salt=salt,
            iterations=100000,
        )

        aes_key = kdf.derive(passphrase.encode("utf-8"))
        aesgcm = AESGCM(aes_key)

        ciphertext = aesgcm.encrypt(
            iv,
            account_key.encode("utf-8"),
            None
        )

        if os.path.exists(filepath):
            backup_path = filepath + ".bak"
            os.replace(filepath, backup_path)
            print(f"Gammal key-fil flyttad till: {backup_path}")

        with open(filepath, "wb") as f:
            f.write(header + salt + iv + ciphertext)

        os.chmod(filepath, 0o600)

        self.execute(
            "UPDATE AuthUsers SET AccountKey=? WHERE lower(Username)=lower(?)",
            (account_key, username),
        )

        self.log_activity(
            "owner_generate_account_key",
            f"Generated new account key file for {username}"
        )

        print()
        print("=" * 58)
        print(" ACCOUNT KEY CREATED")
        print("=" * 58)
        print(f"User        : {username}")
        print(f"Key file    : {filepath}")
        print(f"Permissions : 600")
        print(f"Database    : updated")
        print()
        print("Send to user:")
        print(f"1. File       : {filepath}")
        print(f"2. Passphrase : {passphrase}")
        print()
        print("Tips: skicka fil och passphrase i olika kanaler.")
        print("=" * 58)





    def list_users_without_account_key(self):
        self.print_header("Account key overview")

        rows = self.query_all(
            """
            SELECT
                u.Username,
                u.Status,
                u.AccountKey,
                u.CreatedAt,
                (
                    SELECT a.Timestamp
                    FROM ActivityLog a
                    WHERE lower(a.Username)=lower(u.Username)
                      AND lower(a.Action)='login'
                    ORDER BY a.Id DESC
                    LIMIT 1
                ) AS LatestLogin
            FROM AuthUsers u
            ORDER BY u.Username ASC
            LIMIT 500
            """
        )

        printable = []

        for row in rows:

            key = row["AccountKey"] or ""

            if len(key) >= 8:
                key_preview = key[:8] + "..."
            else:
                key_preview = "-"

            printable.append([
                row["Username"],
                row["Status"] or "verified",
                "yes" if key else "no",
                key_preview,
                self.short_dt(row["CreatedAt"]),
                self.short_dt(row["LatestLogin"]),
            ])

        self.print_rows(
            [
                "Username",
                "Status",
                "Has key",
                "AccountKey",
                "Created",
                "Latest login"
            ],
            printable
        )

        print(f"\nVisar {len(rows)} användare.")


    def _create_key_file_for_username(self, username: str, passphrase: str, output_dir: str):
        account_key = str(uuid.uuid4())

        header = b"RSK1"
        salt = os.urandom(16)
        iv = os.urandom(12)

        kdf = PBKDF2HMAC(
            algorithm=hashes.SHA256(),
            length=32,
            salt=salt,
            iterations=100000,
        )

        aes_key = kdf.derive(passphrase.encode("utf-8"))
        aesgcm = AESGCM(aes_key)
        ciphertext = aesgcm.encrypt(iv, account_key.encode("utf-8"), None)

        filepath = os.path.join(output_dir, f"{username}.key")

        if os.path.exists(filepath):
            backup_path = filepath + ".bak"
            os.replace(filepath, backup_path)

        with open(filepath, "wb") as f:
            f.write(header + salt + iv + ciphertext)

        os.chmod(filepath, 0o600)
        return account_key, filepath

    def bulk_generate_account_keys(self):
        self.print_header("Bulk generate account keys")

        print("1. Alla användare utan AccountKey")
        print("2. Flera användarnamn")
        print("0. Avbryt")

        mode = self.prompt("Val", default="1").strip()

        if mode == "0":
            return

        users = []

        if mode == "1":
            users = self.query_all(
                """
                SELECT Username
                FROM AuthUsers
                WHERE AccountKey IS NULL OR trim(AccountKey)=''
                ORDER BY Username ASC
                """
            )

        elif mode == "2":
            raw = self.prompt("Usernames, kommaseparerade").strip()
            names = [x.strip().lower() for x in raw.split(",") if x.strip()]

            if not names:
                print("Inga usernames.")
                return

            placeholders = ",".join("?" for _ in names)
            users = self.query_all(
                f"""
                SELECT Username
                FROM AuthUsers
                WHERE lower(Username) IN ({placeholders})
                ORDER BY Username ASC
                """,
                names
            )

        else:
            print("Ogiltigt val.")
            return

        if not users:
            print("Inga användare matchade.")
            return

        output_dir = self.prompt(
            "Output folder",
            default="/root/RunSpace/NewServer/generated_keys"
        ).strip()

        os.makedirs(output_dir, exist_ok=True)

        print()
        print(f"Matchade användare: {len(users)}")
        self.print_rows(["Username"], [[u["Username"]] for u in users[:100]])

        if len(users) > 100:
            print(f"... och {len(users) - 100} till.")

        print()
        print("Passphrase-läge:")
        print("1. Auto-generera unik passphrase per användare")
        print("2. Samma passphrase för alla")
        print("0. Avbryt")

        pass_mode = self.prompt("Val", default="1").strip()

        if pass_mode == "0":
            return

        shared_passphrase = None
        if pass_mode == "2":
            shared_passphrase = getpass("Gemensam passphrase: ").strip()
            if not shared_passphrase:
                print("Passphrase krävs.")
                return
            confirm_passphrase = getpass("Bekräfta passphrase: ").strip()
            if shared_passphrase != confirm_passphrase:
                print("Passphrase matchar inte.")
                return

        print()
        print("Detta kommer skapa .key-filer och uppdatera AuthUsers.AccountKey.")
        if not self.confirm("Fortsätt?"):
            print("Avbrutet.")
            return

        csv_path = os.path.join(output_dir, "key_export.csv")
        created = []

        alphabet = string.ascii_letters + string.digits + "-_!?"

        with self.connect() as conn:
            for user in users:
                username = user["Username"].strip().lower()

                if shared_passphrase:
                    passphrase = shared_passphrase
                else:
                    passphrase = "".join(secrets.choice(alphabet) for _ in range(24))

                account_key, filepath = self._create_key_file_for_username(
                    username,
                    passphrase,
                    output_dir
                )

                conn.execute(
                    "UPDATE AuthUsers SET AccountKey=? WHERE lower(Username)=lower(?)",
                    (account_key, username)
                )

                created.append({
                    "username": username,
                    "account_key": account_key,
                    "passphrase": passphrase,
                    "key_file": filepath,
                })

            conn.commit()

        with open(csv_path, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(
                f,
                fieldnames=["username", "account_key", "passphrase", "key_file"]
            )
            writer.writeheader()
            writer.writerows(created)

        os.chmod(csv_path, 0o600)

        self.log_activity(
            "owner_bulk_generate_account_keys",
            f"Generated account keys for {len(created)} users. Output: {output_dir}"
        )

        print()
        print("=" * 58)
        print(" BULK ACCOUNT KEYS CREATED")
        print("=" * 58)
        print(f"Created     : {len(created)}")
        print(f"Folder      : {output_dir}")
        print(f"CSV         : {csv_path}")
        print("Permissions : key files + csv set to 600")
        print()
        print("OBS: CSV innehåller passphrases. Hantera som hemligt material.")
        print("=" * 58)


    def init_key_change_tokens_table(self):
        self.execute("""
            CREATE TABLE IF NOT EXISTS KeyChangeTokens (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TokenHash TEXT NOT NULL UNIQUE,
                Username TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                UsedAt TEXT,
                CreatedBy TEXT
            )
        """)

    def generate_key_change_link(self):
        self.print_header("Generate passphrase change link")
        self.init_key_change_tokens_table()

        username = self.prompt("Username").strip().lower()
        row = self.query_one(
            "SELECT Username, AccountKey FROM AuthUsers WHERE lower(Username)=lower(?) LIMIT 1",
            (username,)
        )

        if not row:
            print("Användaren hittades inte.")
            return

        if not row["AccountKey"]:
            print("Användaren saknar AccountKey.")
            return

        minutes_raw = self.prompt("Länken gäller minuter", default="30")
        try:
            minutes = max(5, min(1440, int(minutes_raw)))
        except ValueError:
            minutes = 30

        from datetime import timedelta

        token = secrets.token_urlsafe(32)
        token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()

        now = datetime.now(timezone.utc)
        expires = now + timedelta(minutes=minutes)

        self.execute(
            """
            INSERT INTO KeyChangeTokens
            (TokenHash, Username, CreatedAt, ExpiresAt, UsedAt, CreatedBy)
            VALUES (?, ?, ?, ?, NULL, ?)
            """,
            (
                token_hash,
                username,
                now.isoformat(),
                expires.isoformat(),
                self.owner_username
            )
        )

        self.log_activity(
            "owner_key_change_link",
            f"Created passphrase-change link for {username}, expires {expires.isoformat()}"
        )

        print()
        print("=" * 58)
        print(" PASSPHRASE CHANGE LINK CREATED")
        print("=" * 58)
        print(f"User      : {username}")
        print(f"Expires   : {self.short_dt(expires.isoformat())} UTC")
        print()
        print("Skicka denna länk till användaren:")
        print(f"https://runspace.cloud/key-change/?token={token}")
        print()
        print("OBS: Länken byter bara passphrase på .key-filen lokalt i browsern.")
        print("Databasens AccountKey ändras inte.")
        print("=" * 58)


    def show_latest_key_resets(self):
        self.print_header("Latest account key resets")

        rows = self.query_all(
            """
            SELECT Id, Username, Action, Details, Timestamp
            FROM ActivityLog
            WHERE Action IN (
                'owner_generate_account_key',
                'owner_bulk_generate_account_keys'
            )
            ORDER BY Id DESC
            LIMIT 100
            """
        )

        if not rows:
            print("Inga key-reset logs hittades.")
            return

        self.print_rows(
            ["ID", "Actor", "Action", "Details", "Timestamp"],
            [[
                r["Id"],
                r["Username"],
                r["Action"],
                r["Details"],
                self.short_dt(r["Timestamp"]),
            ] for r in rows]
        )

        print(f"\nVisar {len(rows)} logs.")

    # ---------------------------
    # Tickets
    # ---------------------------
    def list_tickets(self, status: str | None = None):
        sql = "SELECT Id, TicketId, Username, Category, Subject, Status, CreatedAt FROM SupportTickets"
        params = []
        if status and status != "all":
            sql += " WHERE lower(Status)=?"
            params.append(status.lower())
        sql += " ORDER BY Id DESC LIMIT 200"
        rows = self.query_all(sql, params)

        printable = []
        for row in rows:
            printable.append([
                row["Id"], row["TicketId"], row["Username"], row["Category"], row["Subject"], row["Status"], self.short_dt(row["CreatedAt"])
            ])

        self.print_header("Support Tickets")
        self.print_rows(["ID", "Ticket", "Username", "Category", "Subject", "Status", "Created"], printable)
        print(f"\nVisar {len(rows)} tickets.")

    def show_ticket_details(self):
        ticket_id = self.prompt("Database ID för ticket")
        row = self.query_one("SELECT * FROM SupportTickets WHERE Id=?", (ticket_id,))
        if not row:
            print("Ticket hittades inte.")
            return
        self.print_header(f"Ticket {row['TicketId']}")
        print(f"DB ID      : {row['Id']}")
        print(f"Username   : {row['Username']}")
        print(f"Category   : {row['Category']}")
        print(f"Subject    : {row['Subject']}")
        print(f"Status     : {row['Status']}")
        print(f"CreatedAt  : {self.short_dt(row['CreatedAt'])}")
        print("Description:")
        print(row["Description"] or "-")

    def update_ticket_status(self):
        ticket_id = self.prompt("Database ID för ticket")
        row = self.query_one("SELECT * FROM SupportTickets WHERE Id=?", (ticket_id,))
        if not row:
            print("Ticket hittades inte.")
            return

        new_status = self.prompt("Ny status (open/in_progress/closed)").strip().lower()
        if new_status not in {"open", "in_progress", "closed"}:
            print("Ogiltig status.")
            return

        self.execute("UPDATE SupportTickets SET Status=? WHERE Id=?", (new_status, ticket_id))
        self.log_activity("owner_ticket_status", f"Ticket {ticket_id} -> {new_status}")
        print("Ticket-status uppdaterad.")

    # ---------------------------
    # Activity logs
    # ---------------------------
    def show_logs(self):
        limit = self.prompt("Antal loggrader", default="100")
        try:
            limit_i = max(1, min(1000, int(limit)))
        except ValueError:
            limit_i = 100

        rows = self.query_all(
            "SELECT Id, Username, Action, Details, Timestamp FROM ActivityLog ORDER BY Id DESC LIMIT ?",
            (limit_i,),
        )
        printable = []
        for row in rows:
            printable.append([
                row["Id"], row["Username"], row["Action"], row["Details"], self.short_dt(row["Timestamp"])
            ])

        self.print_header("Activity Log")
        self.print_rows(["ID", "Username", "Action", "Details", "Timestamp"], printable)
        print(f"\nVisar {len(rows)} loggrader.")

    # ---------------------------
    # Settings
    # ---------------------------
    def configure(self):
        self.print_header("Configuration")
        print(f"1. Database path   : {self.db_path}")
        print(f"2. Owner username  : {self.owner_username}")
        print(f"3. System sender   : {self.system_sender}")
        print("4. Tillbaka")
        choice = self.prompt("Val")

        if choice == "1":
            self.db_path = self.prompt("Ny database path", default=self.db_path)
        elif choice == "2":
            self.owner_username = self.prompt("Ny owner username", default=self.owner_username).strip().lower()
        elif choice == "3":
            self.system_sender = self.prompt("Ny system sender", default=self.system_sender).strip().lower()


    # ---------------------------
    # Reports
    # ---------------------------
    def list_reports(self, status: str | None = None):
        sql = "SELECT Id, ReportedBy, ReportedUser, Reason, Details, MessageId, Status, CreatedAt FROM UserReports"
        params = []
        if status and status != "all":
            sql += " WHERE lower(Status)=?"
            params.append(status.lower())
        sql += " ORDER BY Id DESC LIMIT 200"
        rows = self.query_all(sql, params)
        if not rows:
            print("Inga rapporter hittades.")
            return
        printable = []
        for row in rows:
            printable.append([
                str(row["Id"]), row["ReportedBy"], row["ReportedUser"],
                row["Reason"], (row["Details"] or "")[:40],
                str(row["MessageId"] or "-"), row["Status"],
                self.short_dt(row["CreatedAt"])
            ])
        self.print_header("User Reports")
        self.print_rows(["ID", "Rapporterad av", "Rapporterad", "Anledning", "Detaljer", "MsgID", "Status", "Skapad"], printable)
        print(f"\nVisar {len(rows)} rapporter.")

    def handle_report(self):
        report_id = self.prompt("Report ID").strip()
        row = self.query_one("SELECT * FROM UserReports WHERE Id=?", (report_id,))
        if not row:
            print("Rapporten hittades inte.")
            return
        self.print_header(f"Report #{row['Id']}")
        print(f"Rapporterad av : {row['ReportedBy']}")
        print(f"Rapporterad    : {row['ReportedUser']}")
        print(f"Anledning      : {row['Reason']}")
        print(f"Detaljer       : {row['Details'] or '-'}")
        print(f"Meddelande ID  : {row['MessageId'] or '-'}")
        print(f"Status         : {row['Status']}")
        print(f"Skapad         : {self.short_dt(row['CreatedAt'])}")
        print()
        print("1. Markera som granskad (closed)")
        print("2. Banna rapporterad användare")
        print("3. Ignorera (ta bort rapport)")
        print("0. Avbryt")
        choice = self.prompt("Val")
        if choice == "1":
            self.execute("UPDATE UserReports SET Status='closed' WHERE Id=?", (report_id,))
            self.log_activity("owner_close_report", f"Closed report #{report_id} against {row['ReportedUser']}")
            print("Rapport markerad som granskad.")
        elif choice == "2":
            self.execute("UPDATE AuthUsers SET Status='banned' WHERE lower(Username)=lower(?)", (row["ReportedUser"],))
            self.execute("UPDATE UserReports SET Status='closed' WHERE Id=?", (report_id,))
            self.log_activity("owner_ban_from_report", f"Banned {row['ReportedUser']} via report #{report_id}")
            print(f"{row['ReportedUser']} bannad och rapport stängd.")
        elif choice == "3":
            if self.confirm(f"Ta bort rapport #{report_id}?"):
                self.execute("DELETE FROM UserReports WHERE Id=?", (report_id,))
                print("Rapport borttagen.")
        else:
            print("Avbrutet.")


    # ---------------------------
    # Timeout
    # ---------------------------
    def timeout_user(self):
        row = self.select_user()
        if not row:
            return
        username = row["Username"]
        current_lock = row["AccountLockedUntil"] if "AccountLockedUntil" in row.keys() else ""
        if current_lock:
            print(f"Nuvarande timeout: {self.short_dt(current_lock)}")
        print("1. 10 minuter")
        print("2. 30 minuter")
        print("3. 1 timme")
        print("4. 24 timmar")
        print("5. Anpassat antal minuter")
        print("6. Ta bort timeout")
        print("0. Avbryt")
        choice = self.prompt("Val")
        minutes = None
        if choice == "1": minutes = 10
        elif choice == "2": minutes = 30
        elif choice == "3": minutes = 60
        elif choice == "4": minutes = 1440
        elif choice == "5":
            try: minutes = int(self.prompt("Antal minuter"))
            except ValueError: print("Ogiltigt."); return
        elif choice == "6":
            self.execute("UPDATE AuthUsers SET AccountLockedUntil='' WHERE lower(Username)=lower(?)", (username,))
            self.log_activity("owner_untimeout", f"Removed timeout for {username}")
            print(f"Timeout borttagen for {username}.")
            return
        elif choice == "0":
            return
        else:
            print("Ogiltigt val."); return

        if minutes and minutes > 0:
            from datetime import timedelta
            until = datetime.now(timezone.utc) + timedelta(minutes=minutes)
            until_str = until.isoformat()
            self.execute("UPDATE AuthUsers SET AccountLockedUntil=? WHERE lower(Username)=lower(?)", (until_str, username))
            self.log_activity("owner_timeout", f"Timed out {username} for {minutes} minutes until {until_str}")
            print(f"{username} tystad i {minutes} minuter (till {self.short_dt(until_str)}).")

    # ---------------------------
    # Main menu
    # ---------------------------
    def run(self):
        while True:
            try:
                self.print_header("RunSpace Owner CLI")
                print("1. Overview")
                print("2. Visa alla användare")
                print("3. Sök användare")
                print("4. Visa användardetaljer")
                print("5. Byt användarnamn")
                print("6. Redigera bio")
                print("7. Sätt avatar URL")
                print("8. Banna användare")
                print("9. Unbanna användare")
                print("10. Sätt verified")
                print("11. Redigera badges")
                print("12. Ta bort användare")
                print("12b. Massradera användare efter mönster/längd")
                print("13. Skicka systemmeddelande")
                print("14. Visa tickets")
                print("15. Visa ticket-detaljer")
                print("16. Ändra ticket-status")
                print("17. Visa activity logs")
                print("18. Konfiguration")
                print("19. Radera misstänkta användarnamn")
                print("20. Visa rapporter")
                print("21. Hantera rapport")
                print("22. Tysta anvandare (timeout)")
                print("23. Mass add badge")
                print("24. Mass remove badge")
                print("25. Generera account key-fil")
                print("26. Lista användare utan account key")
                print("27. Massgenerera account keys")
                print("28. Visa senaste key-resets")
                print("29. Skapa länk för passphrase-byte")
                print("0. Avsluta")

                choice = self.prompt("Val")

                if choice == "1":
                    self.show_overview()
                elif choice == "2":
                    self.list_users(limit=None)
                elif choice == "3":
                    q = self.prompt("Sökterm")
                    self.list_users(query=q, limit=None)
                elif choice == "4":
                    self.show_user_details()
                elif choice == "5":
                    self.rename_user()
                elif choice == "6":
                    self.edit_bio()
                elif choice == "7":
                    self.set_avatar_url()
                elif choice == "8":
                    self.ban_user()
                elif choice == "9":
                    self.unban_user()
                elif choice == "10":
                    self.set_verified()
                elif choice == "11":
                    self.edit_badges()
                elif choice == "12":
                    self.delete_user()
                elif choice.lower() == "12b":
                    self.bulk_delete_users()
                elif choice == "13":
                    self.send_system_message()
                elif choice == "14":
                    status = self.prompt("Filter (all/open/in_progress/closed)", default="all").strip().lower()
                    self.list_tickets(status=status)
                elif choice == "15":
                    self.show_ticket_details()
                elif choice == "16":
                    self.update_ticket_status()
                elif choice == "17":
                    self.show_logs()
                elif choice == "18":
                    self.configure()
                elif choice == "19":
                    self.bulk_delete_suspicious_usernames()
                elif choice == "20":
                    status = self.prompt("Filter (all/open/closed)", default="open").strip().lower()
                    self.list_reports(status=status)
                elif choice == "21":
                    self.handle_report()
                elif choice == "22":
                    self.timeout_user()
                elif choice == "23":
                    self.bulk_add_badge()
                elif choice == "24":
                    self.bulk_remove_badge()
                elif choice == "25":
                    self.generate_account_key_for_user()
                elif choice == "26":
                    self.list_users_without_account_key()
                elif choice == "27":
                    self.bulk_generate_account_keys()
                elif choice == "28":
                    self.show_latest_key_resets()
                elif choice == "29":
                    self.generate_key_change_link()
                elif choice == "0":
                    print("Hejdå.")
                    break
                else:
                    print("Ogiltigt val.")

            except KeyboardInterrupt:
                print("\nAvbrutet.")
            except Exception as e:
                print(f"Fel: {e}")

            input("\nTryck Enter för att fortsätta...")


def main():
    app = RunSpaceOwnerCLI()
    if len(sys.argv) > 1:
        app.db_path = sys.argv[1]
    app.run()


if __name__ == "__main__":
    main()
