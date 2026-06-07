import threading
import time
import logging
from datetime import datetime
from typing import Callable, Optional, List, Set
from .http_client import HttpClient
from .models import Message, Member, Server, Channel, BotEvent
from .exceptions import RunSpaceError

logger = logging.getLogger("runspace")


class RunSpaceBot:
    """
    RunSpace Bot SDK

    Example usage:
        from runspace import RunSpaceBot

        bot = RunSpaceBot(
            token="your-bot-token",
            server_id="your-server-id",
            base_url="https://runspace.cloud"
        )

        @bot.on_message
        def handle_message(msg):
            if msg.text.startswith("!hello"):
                bot.send(msg.channel_id, f"Hej @{msg.author}!")

        bot.run()
    """

    def __init__(self, token: str, server_id: str, base_url: str = "https://runspace.cloud"):
        self.token = token
        self.server_id = server_id
        self.base_url = base_url
        self.http = HttpClient(base_url, token)

        # Event handlers
        self._on_message_handlers: List[Callable] = []
        self._on_member_join_handlers: List[Callable] = []
        self._on_member_leave_handlers: List[Callable] = []
        self._on_ready_handlers: List[Callable] = []

        # Moderation state
        self.ip_blacklist: Set[str] = set()
        self.word_blacklist: Set[str] = set()
        self.banned_users: Set[str] = set()

        # Polling state
        self._running = False
        self._poll_interval = 2.0
        self._last_event_id = 0

        # Server info cache
        self._server: Optional[Server] = None

    # ── Decorators ──────────────────────────────────────────

    def on_message(self, func: Callable):
        """Register a message handler."""
        self._on_message_handlers.append(func)
        return func

    def on_member_join(self, func: Callable):
        """Register a member join handler."""
        self._on_member_join_handlers.append(func)
        return func

    def on_member_leave(self, func: Callable):
        """Register a member leave handler."""
        self._on_member_leave_handlers.append(func)
        return func

    def on_ready(self, func: Callable):
        """Register a ready handler (called when bot starts)."""
        self._on_ready_handlers.append(func)
        return func

    # ── Actions ─────────────────────────────────────────────

    def send(self, channel_id: str, text: str) -> dict:
        """Send a message to a channel."""
        return self.http.post(
            f"/api/bot/groups/{self.server_id}/channels/{channel_id}/send",
            json={"text": text}
        )

    def kick(self, username: str) -> bool:
        """Kick a member from the server."""
        try:
            self.http.post(f"/api/groups/{self.server_id}/kick/{username}")
            logger.info(f"[Bot] Kicked {username}")
            return True
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to kick {username}: {e}")
            return False

    def ban_ip(self, ip: str):
        """Add IP to local blacklist and notify server."""
        self.ip_blacklist.add(ip)
        try:
            self.http.post(
                f"/api/bot/groups/{self.server_id}/blacklist/ip",
                json={"ip": ip}
            )
            logger.info(f"[Bot] Blacklisted IP: {ip}")
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to blacklist IP {ip}: {e}")

    def unban_ip(self, ip: str):
        """Remove IP from blacklist."""
        self.ip_blacklist.discard(ip)
        try:
            self.http.delete(f"/api/bot/groups/{self.server_id}/blacklist/ip/{ip}")
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to unban IP {ip}: {e}")

    def add_word_filter(self, word: str):
        """Add a word to the auto-delete filter."""
        self.word_blacklist.add(word.lower())
        try:
            self.http.post(
                f"/api/bot/groups/{self.server_id}/filters/words",
                json={"word": word}
            )
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to add word filter: {e}")

    def delete_message(self, message_id: int) -> bool:
        """Delete a message by ID."""
        try:
            self.http.delete(f"/api/bot/groups/{self.server_id}/messages/{message_id}")
            return True
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to delete message {message_id}: {e}")
            return False

    def set_role(self, username: str, role: str) -> bool:
        """Set a member's role."""
        try:
            self.http.post(
                f"/api/groups/{self.server_id}/members/{username}/role",
                json={"role": role}
            )
            return True
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to set role: {e}")
            return False

    def get_members(self) -> List[Member]:
        """Get all server members."""
        try:
            data = self.http.get(f"/api/groups/{self.server_id}")
            return [
                Member(
                    username=m["username"],
                    role=m["role"],
                    server_id=self.server_id,
                    joined_at=m.get("joinedAt")
                )
                for m in data.get("members", [])
            ]
        except RunSpaceError:
            return []

    def get_server(self) -> Optional[Server]:
        """Get server info."""
        try:
            data = self.http.get(f"/api/groups/{self.server_id}")
            self._server = Server(
                group_id=data["groupId"],
                name=data["name"],
                description=data.get("description", ""),
                owner=data.get("owner", ""),
                channels=data.get("channels", []),
                members=data.get("members", [])
            )
            return self._server
        except RunSpaceError as e:
            logger.error(f"[Bot] Failed to get server info: {e}")
            return None

    # ── Internal moderation checks ───────────────────────────

    def _check_message(self, msg: Message) -> bool:
        """Run built-in moderation checks. Returns True if message passed."""
        if msg.ip and msg.ip in self.ip_blacklist:
            logger.warning(f"[Bot] Blocked message from blacklisted IP {msg.ip}")
            self.delete_message(msg.id)
            return False

        if any(word in msg.text.lower() for word in self.word_blacklist):
            logger.warning(f"[Bot] Deleted message with filtered word from {msg.author}")
            self.delete_message(msg.id)
            return False

        return True

    # ── Event polling ────────────────────────────────────────

    def _poll_events(self):
        """Poll for new events from the RunSpace API."""
        while self._running:
            try:
                events = self.http.get(
                    f"/api/bot/groups/{self.server_id}/events",
                    params={"after": self._last_event_id}
                )
                for event in events or []:
                    self._dispatch_event(event)
                    self._last_event_id = max(self._last_event_id, event.get("id", 0))
            except RunSpaceError as e:
                logger.error(f"[Bot] Poll error: {e}")
            except Exception as e:
                logger.error(f"[Bot] Unexpected poll error: {e}")
            time.sleep(self._poll_interval)

    def _dispatch_event(self, raw: dict):
        """Dispatch a raw event to handlers."""
        event_type = raw.get("type", "")

        if event_type == "message":
            msg = Message(
                id=raw.get("id", 0),
                text=raw.get("text", ""),
                author=raw.get("from", ""),
                server_id=self.server_id,
                channel_id=raw.get("channelId", ""),
                timestamp=datetime.fromisoformat(raw.get("ts", datetime.utcnow().isoformat())),
                ip=raw.get("ip")
            )
            # Built-in checks first
            if not self._check_message(msg):
                return
            # User handlers
            for handler in self._on_message_handlers:
                try:
                    handler(msg)
                except Exception as e:
                    logger.error(f"[Bot] Handler error: {e}")

        elif event_type == "member_join":
            member = Member(
                username=raw.get("username", ""),
                role=raw.get("role", "member"),
                server_id=self.server_id
            )
            for handler in self._on_member_join_handlers:
                try:
                    handler(member)
                except Exception as e:
                    logger.error(f"[Bot] Handler error: {e}")

        elif event_type == "member_leave":
            member = Member(
                username=raw.get("username", ""),
                role=raw.get("role", "member"),
                server_id=self.server_id
            )
            for handler in self._on_member_leave_handlers:
                try:
                    handler(member)
                except Exception as e:
                    logger.error(f"[Bot] Handler error: {e}")

    # ── Lifecycle ────────────────────────────────────────────

    def run(self, poll_interval: float = 2.0):
        """
        Start the bot. Blocks until stopped with Ctrl+C.

        Args:
            poll_interval: How often to poll for events (seconds).
        """
        self._poll_interval = poll_interval
        self._running = True

        logger.info(f"[Bot] Starting — server: {self.server_id}")

        # Verify token
        try:
            info = self.http.get(f"/api/bot/verify")
            logger.info(f"[Bot] Authenticated as: {info.get('botName', '?')}")
        except RunSpaceError as e:
            logger.error(f"[Bot] Auth failed: {e}")
            return

        # Fire ready handlers
        server = self.get_server()
        for handler in self._on_ready_handlers:
            try:
                handler(server)
            except Exception as e:
                logger.error(f"[Bot] Ready handler error: {e}")

        logger.info("[Bot] Running. Press Ctrl+C to stop.")

        # Start polling in background thread
        poll_thread = threading.Thread(target=self._poll_events, daemon=True)
        poll_thread.start()

        try:
            while self._running:
                time.sleep(0.5)
        except KeyboardInterrupt:
            logger.info("[Bot] Stopping…")
            self._running = False

    def stop(self):
        """Stop the bot programmatically."""
        self._running = False
