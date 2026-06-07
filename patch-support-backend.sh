#!/bin/bash
# ═══════════════════════════════════════════════
# patch-support-backend.sh - Phase 1
# Migrates DB + patches Program.cs for Support Inbox
# ═══════════════════════════════════════════════

set -e

PROGRAM_CS="/root/RunSpace/NewServer/Program.cs"
DB_PATH="/root/RunSpace/data/runspace.db"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
PROGRAM_BACKUP="$PROGRAM_CS.bak.$TIMESTAMP"
DB_BACKUP="$DB_PATH.bak.$TIMESTAMP"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo -e "${BLUE}  Support Inbox - Phase 1 (Backend + DB)${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════${NC}"
echo ""

# ── Step 1: Verify files exist ──
[ ! -f "$PROGRAM_CS" ] && { echo -e "${RED}✗ Program.cs not found${NC}"; exit 1; }
[ ! -f "$DB_PATH" ] && { echo -e "${RED}✗ Database not found at $DB_PATH${NC}"; exit 1; }
echo -e "${GREEN}✓${NC} Found Program.cs and database"

# ── Step 2: Check if already migrated ──
if sqlite3 "$DB_PATH" ".schema AuthUsers" | grep -q "IsAdmin"; then
    echo -e "${YELLOW}⚠ IsAdmin column already exists${NC}"
    ALREADY_MIGRATED=1
else
    ALREADY_MIGRATED=0
fi

if grep -q "/api/support/my-tickets" "$PROGRAM_CS"; then
    echo -e "${YELLOW}⚠ Program.cs already has support-inbox endpoints${NC}"
    ALREADY_PATCHED=1
else
    ALREADY_PATCHED=0
fi

if [ $ALREADY_MIGRATED -eq 1 ] && [ $ALREADY_PATCHED -eq 1 ]; then
    echo -e "${YELLOW}Everything already deployed. Exiting.${NC}"
    exit 0
fi

# ── Step 3: Backups ──
cp "$PROGRAM_CS" "$PROGRAM_BACKUP"
cp "$DB_PATH" "$DB_BACKUP"
echo -e "${GREEN}✓${NC} Backups created:"
echo "  - $PROGRAM_BACKUP"
echo "  - $DB_BACKUP"

# ── Step 4: Stop the service while we modify things ──
echo -e "${BLUE}→${NC} Stopping runspace.service..."
sudo systemctl stop runspace.service
echo -e "${GREEN}✓${NC} Service stopped"

# ── Step 5: DB migration ──
if [ $ALREADY_MIGRATED -eq 0 ]; then
    echo -e "${BLUE}→${NC} Migrating database..."
    sqlite3 "$DB_PATH" <<'SQL'
ALTER TABLE AuthUsers ADD COLUMN IsAdmin INTEGER NOT NULL DEFAULT 0;
UPDATE AuthUsers SET IsAdmin = 1 WHERE LOWER(Username) IN ('mx403', 'mxssy');
ALTER TABLE SupportTickets ADD COLUMN UpdatedAt TEXT;
ALTER TABLE SupportTickets ADD COLUMN AssignedTo TEXT;
ALTER TABLE SupportTickets ADD COLUMN Priority TEXT NOT NULL DEFAULT 'normal';
ALTER TABLE SupportTickets ADD COLUMN UnreadByUser INTEGER NOT NULL DEFAULT 0;
ALTER TABLE SupportTickets ADD COLUMN UnreadByAdmin INTEGER NOT NULL DEFAULT 1;
UPDATE SupportTickets SET UpdatedAt = CreatedAt WHERE UpdatedAt IS NULL;
CREATE TABLE IF NOT EXISTS SupportMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TicketId TEXT NOT NULL,
    FromUsername TEXT NOT NULL,
    Message TEXT NOT NULL,
    IsAdminReply INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SupportMessages_TicketId ON SupportMessages(TicketId);
CREATE INDEX IF NOT EXISTS IX_SupportMessages_CreatedAt ON SupportMessages(CreatedAt);
CREATE INDEX IF NOT EXISTS IX_SupportTickets_Username ON SupportTickets(Username);
CREATE INDEX IF NOT EXISTS IX_SupportTickets_UpdatedAt ON SupportTickets(UpdatedAt);
SQL
    echo -e "${GREEN}✓${NC} Database migrated"

    # Verify
    ADMIN_COUNT=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM AuthUsers WHERE IsAdmin = 1;")
    echo -e "${GREEN}✓${NC} $ADMIN_COUNT admin(s) configured"
else
    echo -e "${YELLOW}⚠${NC} Skipping DB migration (already done)"
fi

# ── Step 6: Patch Program.cs ──
if [ $ALREADY_PATCHED -eq 0 ]; then
    echo -e "${BLUE}→${NC} Patching Program.cs..."

    python3 <<'PYEOF'
import sys
import re

PROGRAM_CS = "/root/RunSpace/NewServer/Program.cs"

with open(PROGRAM_CS, 'r', encoding='utf-8') as f:
    content = f.read()

# ── PATCH 1: Update IsAdmin() to read from DB ──
OLD_ISADMIN = 'public static bool IsAdmin(string? u) { var t = (u ?? "").Trim().ToLowerInvariant(); return t == "mx403" || t == "mxssy"; }'
NEW_ISADMIN = '''public static bool IsAdmin(string? u)
    {
        var t = (u ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t)) return false;
        try
        {
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = "SELECT IsAdmin FROM AuthUsers WHERE LOWER(Username) = $u LIMIT 1";
            c.Parameters.AddWithValue("$u", t);
            var result = c.ExecuteScalar();
            if (result != null && result != System.DBNull.Value)
                return System.Convert.ToInt32(result) == 1;
        }
        catch { }
        return t == "mx403" || t == "mxssy";
    }'''

if OLD_ISADMIN not in content:
    print("ERROR: Could not find IsAdmin() to replace", file=sys.stderr)
    sys.exit(1)
content = content.replace(OLD_ISADMIN, NEW_ISADMIN, 1)

# ── PATCH 2: Replace /api/support/ticket endpoint ──
# Match the old ticket endpoint and replace it
OLD_TICKET_PATTERN = re.compile(
    r'app\.MapPost\("/api/support/ticket",.*?return Results\.Ok\(new \{ ticketId, status = "open", message = "Ticket created\." \}\);\s*\}\);',
    re.DOTALL
)

NEW_TICKET_CODE = '''app.MapPost("/api/support/ticket", async (HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(user, "support_ticket", 5, 3600))
        return Results.Json(new { message = "Too many tickets. Please wait before creating another." }, statusCode: 429);

    SupportTicketReq? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportTicketReq>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var username = InputSanitizer.SanitizeInput((body?.Username ?? user).Trim(), 50);
    var category = InputSanitizer.SanitizeInput(body?.Category?.Trim() ?? "", 100);
    var subject = InputSanitizer.SanitizeInput(body?.Subject?.Trim() ?? "", 200);
    var description = InputSanitizer.SanitizeInput(body?.Description?.Trim() ?? "", 4000);

    if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(description))
        return Results.BadRequest(new { message = "Category, subject and description are required." });

    var ticketId = "RS-" + DateTime.UtcNow.Ticks.ToString()[^5..] + "-" +
                   Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
    var now = DateTime.UtcNow.ToString("o");

    using (var db = DbHelpers.OpenDb())
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"INSERT INTO SupportTickets (TicketId, Username, Category, Subject, Description, Status, Priority, CreatedAt, UpdatedAt, UnreadByAdmin) VALUES ($tid, $u, $c, $s, $d, 'open', 'normal', $now, $now, 1)";
        cmd.Parameters.AddWithValue("$tid", ticketId);
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$c", category);
        cmd.Parameters.AddWithValue("$s", subject);
        cmd.Parameters.AddWithValue("$d", description);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    var admins = new List<string>();
    using (var db = DbHelpers.OpenDb())
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT LOWER(Username) FROM AuthUsers WHERE IsAdmin = 1";
        using var r = cmd.ExecuteReader();
        while (r.Read()) admins.Add(r.GetString(0));
    }

    var payload = new { ticketId, username, category, subject, createdAt = now, isNew = true };
    foreach (var admin in admins)
    {
        try { await hub.Clients.User(admin).SendAsync("SupportAdminUpdate", payload); } catch { }
    }

    AppHelpers.LogActivity(user, "support_ticket_created", $"Ticket {ticketId}: {subject}");
    return Results.Ok(new { success = true, ticketId, status = "open", message = "Ticket created." });
});'''

match = OLD_TICKET_PATTERN.search(content)
if not match:
    print("ERROR: Could not find /api/support/ticket endpoint to replace", file=sys.stderr)
    sys.exit(1)
content = content[:match.start()] + NEW_TICKET_CODE + content[match.end():]

# ── PATCH 3: Insert new endpoints before /api/admin/support/tickets ──
NEW_ENDPOINTS = '''
// ═══════════════════════════════════════════════
// SUPPORT INBOX — user + admin endpoints
// ═══════════════════════════════════════════════

app.MapGet("/api/support/my-tickets", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();

    var tickets = new List<object>();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT TicketId, Category, Subject, Status, Priority, CreatedAt, UpdatedAt, UnreadByUser FROM SupportTickets WHERE LOWER(Username) = $u ORDER BY COALESCE(UpdatedAt, CreatedAt) DESC";
    cmd.Parameters.AddWithValue("$u", user);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        tickets.Add(new { ticketId = r.GetString(0), category = r.GetString(1), subject = r.GetString(2), status = r.GetString(3), priority = r.IsDBNull(4) ? "normal" : r.GetString(4), createdAt = r.GetString(5), updatedAt = r.IsDBNull(6) ? r.GetString(5) : r.GetString(6), unread = r.GetInt32(7) });
    }
    return Results.Ok(new { tickets });
});

app.MapGet("/api/support/unread-count", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Ok(new { count = 0 });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(SUM(UnreadByUser), 0) FROM SupportTickets WHERE LOWER(Username) = $u";
    cmd.Parameters.AddWithValue("$u", user);
    return Results.Ok(new { count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0) });
});

app.MapGet("/api/support/tickets/{ticketId}", (string ticketId, HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();

    using var db = DbHelpers.OpenDb();
    object? ticket = null;
    string? ownerUsername = null;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"SELECT TicketId, Username, Category, Subject, Description, Status, Priority, CreatedAt, UpdatedAt, AssignedTo FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", ticketId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Results.NotFound(new { message = "Ticket not found." });
        ownerUsername = r.GetString(1).ToLowerInvariant();
        if (ownerUsername != user && !AppHelpers.IsAdmin(user))
            return Results.Forbid();
        ticket = new { ticketId = r.GetString(0), username = r.GetString(1), category = r.GetString(2), subject = r.GetString(3), description = r.GetString(4), status = r.GetString(5), priority = r.IsDBNull(6) ? "normal" : r.GetString(6), createdAt = r.GetString(7), updatedAt = r.IsDBNull(8) ? r.GetString(7) : r.GetString(8), assignedTo = r.IsDBNull(9) ? null : r.GetString(9) };
    }

    var messages = new List<object>();
    using (var cmd2 = db.CreateCommand())
    {
        cmd2.CommandText = @"SELECT Id, FromUsername, Message, IsAdminReply, CreatedAt FROM SupportMessages WHERE TicketId = $t ORDER BY CreatedAt ASC";
        cmd2.Parameters.AddWithValue("$t", ticketId);
        using var r2 = cmd2.ExecuteReader();
        while (r2.Read())
        {
            messages.Add(new { id = r2.GetInt64(0), fromUsername = r2.GetString(1), message = r2.GetString(2), isAdminReply = r2.GetInt32(3) == 1, createdAt = r2.GetString(4) });
        }
    }

    using (var upd = db.CreateCommand())
    {
        if (ownerUsername == user)
            upd.CommandText = "UPDATE SupportTickets SET UnreadByUser = 0 WHERE TicketId = $t";
        else
            upd.CommandText = "UPDATE SupportTickets SET UnreadByAdmin = 0 WHERE TicketId = $t";
        upd.Parameters.AddWithValue("$t", ticketId);
        upd.ExecuteNonQuery();
    }
    return Results.Ok(new { ticket, messages });
});

app.MapPost("/api/support/tickets/{ticketId}/reply", async (string ticketId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(user, "support_reply", 20, 3600))
        return Results.Json(new { message = "Too many replies. Please wait a while." }, statusCode: 429);

    SupportReplyDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportReplyDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var msg = InputSanitizer.SanitizeInput(body?.Message?.Trim() ?? "", 4000);
    if (string.IsNullOrEmpty(msg)) return Results.BadRequest(new { message = "Message is required." });

    using var db = DbHelpers.OpenDb();
    string? owner = null;
    string? currentStatus = null;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT LOWER(Username), Status FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", ticketId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Results.NotFound(new { message = "Ticket not found." });
        owner = r.GetString(0);
        currentStatus = r.GetString(1);
    }

    if (owner != user) return Results.Forbid();
    if (currentStatus == "closed")
        return Results.BadRequest(new { message = "This ticket is closed and cannot receive replies." });

    var now = DateTime.UtcNow.ToString("o");
    using (var ins = db.CreateCommand())
    {
        ins.CommandText = "INSERT INTO SupportMessages (TicketId, FromUsername, Message, IsAdminReply, CreatedAt) VALUES ($t, $u, $m, 0, $c)";
        ins.Parameters.AddWithValue("$t", ticketId);
        ins.Parameters.AddWithValue("$u", user);
        ins.Parameters.AddWithValue("$m", msg);
        ins.Parameters.AddWithValue("$c", now);
        ins.ExecuteNonQuery();
    }

    using (var upd = db.CreateCommand())
    {
        upd.CommandText = @"UPDATE SupportTickets SET UnreadByAdmin = 1, UpdatedAt = $now, Status = CASE WHEN Status = 'waiting_for_user' THEN 'in_progress' ELSE Status END WHERE TicketId = $t";
        upd.Parameters.AddWithValue("$now", now);
        upd.Parameters.AddWithValue("$t", ticketId);
        upd.ExecuteNonQuery();
    }

    var adminList = new List<string>();
    using (var ac = db.CreateCommand())
    {
        ac.CommandText = "SELECT LOWER(Username) FROM AuthUsers WHERE IsAdmin = 1";
        using var ar = ac.ExecuteReader();
        while (ar.Read()) adminList.Add(ar.GetString(0));
    }

    var payload = new { ticketId, fromUsername = user, isAdminReply = false, createdAt = now };
    foreach (var admin in adminList)
    {
        try { await hub.Clients.User(admin).SendAsync("SupportAdminUpdate", payload); } catch { }
    }

    AppHelpers.LogActivity(user, "support_reply", $"Replied to ticket {ticketId}");
    return Results.Ok(new { success = true });
});

app.MapGet("/api/admin/support/inbox", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    var status = ctx.Request.Query["status"].ToString()?.Trim().ToLowerInvariant() ?? "";
    var assignedTo = ctx.Request.Query["assignedTo"].ToString()?.Trim().ToLowerInvariant() ?? "";
    var search = ctx.Request.Query["search"].ToString()?.Trim() ?? "";

    var tickets = new List<object>();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    var sql = "SELECT TicketId, Username, Category, Subject, Status, Priority, CreatedAt, UpdatedAt, AssignedTo, UnreadByAdmin FROM SupportTickets WHERE 1=1";
    if (!string.IsNullOrEmpty(status) && status != "all") { sql += " AND Status = $status"; cmd.Parameters.AddWithValue("$status", status); }
    if (!string.IsNullOrEmpty(assignedTo))
    {
        if (assignedTo == "unassigned") sql += " AND (AssignedTo IS NULL OR AssignedTo = '')";
        else if (assignedTo == "me") { sql += " AND LOWER(AssignedTo) = $me"; cmd.Parameters.AddWithValue("$me", user); }
        else { sql += " AND LOWER(AssignedTo) = $assignedTo"; cmd.Parameters.AddWithValue("$assignedTo", assignedTo); }
    }
    if (!string.IsNullOrEmpty(search))
    {
        sql += " AND (LOWER(Username) LIKE $s OR LOWER(Subject) LIKE $s OR LOWER(TicketId) LIKE $s)";
        cmd.Parameters.AddWithValue("$s", "%" + search.ToLowerInvariant() + "%");
    }
    sql += " ORDER BY UnreadByAdmin DESC, COALESCE(UpdatedAt, CreatedAt) DESC LIMIT 200";
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        tickets.Add(new { ticketId = r.GetString(0), username = r.GetString(1), category = r.GetString(2), subject = r.GetString(3), status = r.GetString(4), priority = r.IsDBNull(5) ? "normal" : r.GetString(5), createdAt = r.GetString(6), updatedAt = r.IsDBNull(7) ? r.GetString(6) : r.GetString(7), assignedTo = r.IsDBNull(8) ? null : r.GetString(8), unread = r.GetInt32(9) });
    }
    return Results.Ok(new { tickets });
});

app.MapGet("/api/admin/support/stats", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT COUNT(*) FILTER (WHERE Status = 'open'), COUNT(*) FILTER (WHERE Status = 'in_progress'), COUNT(*) FILTER (WHERE Status = 'waiting_for_user'), COUNT(*) FILTER (WHERE Status = 'resolved'), COUNT(*) FILTER (WHERE Status = 'closed'), SUM(UnreadByAdmin), COUNT(*) FILTER (WHERE AssignedTo IS NULL OR AssignedTo = ''), COUNT(*) FILTER (WHERE LOWER(AssignedTo) = $me) FROM SupportTickets";
    cmd.Parameters.AddWithValue("$me", user);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.Ok(new { });
    return Results.Ok(new { open = r.GetInt32(0), inProgress = r.GetInt32(1), waiting = r.GetInt32(2), resolved = r.GetInt32(3), closed = r.GetInt32(4), unread = r.IsDBNull(5) ? 0 : r.GetInt32(5), unassigned = r.GetInt32(6), mine = r.GetInt32(7) });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/reply", async (string ticketId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    SupportReplyDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportReplyDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var msg = InputSanitizer.SanitizeInput(body?.Message?.Trim() ?? "", 4000);
    if (string.IsNullOrEmpty(msg)) return Results.BadRequest(new { message = "Message is required." });

    using var db = DbHelpers.OpenDb();
    string? targetUser = null;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT LOWER(Username) FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", ticketId);
        var u = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(u)) return Results.NotFound(new { message = "Ticket not found." });
        targetUser = u;
    }

    var now = DateTime.UtcNow.ToString("o");
    using (var ins = db.CreateCommand())
    {
        ins.CommandText = "INSERT INTO SupportMessages (TicketId, FromUsername, Message, IsAdminReply, CreatedAt) VALUES ($t, $u, $m, 1, $c)";
        ins.Parameters.AddWithValue("$t", ticketId);
        ins.Parameters.AddWithValue("$u", user!);
        ins.Parameters.AddWithValue("$m", msg);
        ins.Parameters.AddWithValue("$c", now);
        ins.ExecuteNonQuery();
    }

    using (var upd = db.CreateCommand())
    {
        upd.CommandText = @"UPDATE SupportTickets SET UnreadByUser = UnreadByUser + 1, UpdatedAt = $now, Status = CASE WHEN Status = 'open' THEN 'in_progress' ELSE Status END WHERE TicketId = $t";
        upd.Parameters.AddWithValue("$now", now);
        upd.Parameters.AddWithValue("$t", ticketId);
        upd.ExecuteNonQuery();
    }

    var payload = new { ticketId, fromUsername = user, isAdminReply = true, createdAt = now };
    try { await hub.Clients.User(targetUser).SendAsync("SupportTicketUpdate", payload); } catch { }

    AppHelpers.LogActivity(user!, "support_admin_reply", $"Replied to ticket {ticketId}");
    return Results.Ok(new { success = true });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/status", async (string ticketId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    SupportStatusDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportStatusDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var status = (body?.Status ?? "").Trim().ToLowerInvariant();
    var allowed = new[] { "open", "in_progress", "waiting_for_user", "resolved", "closed" };
    if (!allowed.Contains(status)) return Results.BadRequest(new { message = "Invalid status." });

    var now = DateTime.UtcNow.ToString("o");
    string? targetUser = null;
    using var db = DbHelpers.OpenDb();
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "UPDATE SupportTickets SET Status = $s, UpdatedAt = $now, UnreadByUser = UnreadByUser + 1 WHERE TicketId = $t";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$t", ticketId);
        cmd.ExecuteNonQuery();
    }
    using (var cmd2 = db.CreateCommand())
    {
        cmd2.CommandText = "SELECT LOWER(Username) FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd2.Parameters.AddWithValue("$t", ticketId);
        targetUser = cmd2.ExecuteScalar() as string;
    }

    if (targetUser == null) return Results.NotFound(new { message = "Ticket not found." });
    try { await hub.Clients.User(targetUser).SendAsync("SupportTicketUpdate", new { ticketId, statusChanged = true, newStatus = status }); } catch { }

    AppHelpers.LogActivity(user!, "support_status_change", $"Ticket {ticketId} -> {status}");
    return Results.Ok(new { success = true, status });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/assign", async (string ticketId, HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    SupportAssignDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportAssignDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var target = (body?.AssignedTo ?? "").Trim().ToLowerInvariant();
    if (target == "me") target = user!;
    if (!string.IsNullOrEmpty(target) && !AppHelpers.IsAdmin(target))
        return Results.BadRequest(new { message = "Can only assign to admins." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE SupportTickets SET AssignedTo = $a, UpdatedAt = $now WHERE TicketId = $t";
    cmd.Parameters.AddWithValue("$a", string.IsNullOrEmpty(target) ? (object)DBNull.Value : target);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$t", ticketId);
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Ticket not found." });

    AppHelpers.LogActivity(user!, "support_assign", $"Ticket {ticketId} -> {target}");
    return Results.Ok(new { success = true, assignedTo = string.IsNullOrEmpty(target) ? null : target });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/priority", async (string ticketId, HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    SupportPriorityDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportPriorityDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var priority = (body?.Priority ?? "").Trim().ToLowerInvariant();
    var allowed = new[] { "low", "normal", "high", "urgent" };
    if (!allowed.Contains(priority)) return Results.BadRequest(new { message = "Invalid priority." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE SupportTickets SET Priority = $p, UpdatedAt = $now WHERE TicketId = $t";
    cmd.Parameters.AddWithValue("$p", priority);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$t", ticketId);
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Ticket not found." });

    AppHelpers.LogActivity(user!, "support_priority", $"Ticket {ticketId} -> {priority}");
    return Results.Ok(new { success = true, priority });
});

'''

# Find /api/admin/support/tickets (old one we keep, but insert new endpoints BEFORE it)
admin_marker = 'app.MapGet("/api/admin/support/tickets"'
if admin_marker not in content:
    # If old admin endpoint doesn't exist, insert before the last app.Run()
    run_marker = 'app.Run('
    if run_marker not in content:
        print("ERROR: Could not find insertion point for endpoints", file=sys.stderr)
        sys.exit(1)
    content = content.replace(run_marker, NEW_ENDPOINTS + '\n' + run_marker, 1)
else:
    content = content.replace(admin_marker, NEW_ENDPOINTS + '\n' + admin_marker, 1)

# ── PATCH 4: Add DTOs near other records ──
DTO_CODE = '''
public record SupportReplyDto(string? Message);
public record SupportStatusDto(string? Status);
public record SupportAssignDto(string? AssignedTo);
public record SupportPriorityDto(string? Priority);
'''

dto_marker = 'public record SupportTicketReq(string? Username, string? Category, string? Subject, string? Description);'
if dto_marker not in content:
    print("ERROR: Could not find SupportTicketReq to anchor DTOs", file=sys.stderr)
    sys.exit(1)

if "SupportReplyDto" not in content:
    content = content.replace(dto_marker, dto_marker + DTO_CODE, 1)

# Write back
with open(PROGRAM_CS, 'w', encoding='utf-8') as f:
    f.write(content)

# Verify
with open(PROGRAM_CS, 'r', encoding='utf-8') as f:
    verify = f.read()

checks = [
    ('/api/support/my-tickets', 'my-tickets endpoint'),
    ('/api/admin/support/inbox', 'admin inbox endpoint'),
    ('SupportReplyDto', 'Reply DTO'),
    ('SupportMessages', 'SupportMessages reference'),
    ('SELECT IsAdmin FROM AuthUsers', 'IsAdmin DB lookup'),
]
for needle, name in checks:
    if needle not in verify:
        print(f"ERROR: {name} missing after patch", file=sys.stderr)
        sys.exit(1)

print("SUCCESS: All patches applied")
PYEOF

    if [ $? -ne 0 ]; then
        echo -e "${RED}✗ Patch failed. Restoring...${NC}"
        cp "$PROGRAM_BACKUP" "$PROGRAM_CS"
        sudo systemctl start runspace.service
        exit 1
    fi
    echo -e "${GREEN}✓${NC} Program.cs patched"
else
    echo -e "${YELLOW}⚠${NC} Skipping Program.cs patch (already done)"
fi

# ── Step 7: Build ──
echo -e "${BLUE}→${NC} Building..."
cd /root/RunSpace/NewServer

if ! dotnet publish -c Release -o publish; then
    echo -e "${RED}✗ Build failed!${NC}"
    echo -e "${YELLOW}Rolling back...${NC}"
    cp "$PROGRAM_BACKUP" "$PROGRAM_CS"

    if [ $ALREADY_MIGRATED -eq 0 ]; then
        echo -e "${YELLOW}Restoring database...${NC}"
        cp "$DB_BACKUP" "$DB_PATH"
    fi

    sudo systemctl start runspace.service
    exit 1
fi
echo -e "${GREEN}✓${NC} Build succeeded"

# ── Step 8: Start service ──
echo -e "${BLUE}→${NC} Starting service..."
sudo systemctl start runspace.service
sleep 3

if systemctl is-active --quiet runspace.service; then
    echo -e "${GREEN}✓${NC} Service started"
else
    echo -e "${RED}✗ Service failed to start!${NC}"
    echo "Check logs: sudo journalctl -u runspace.service -n 50"
    exit 1
fi

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════${NC}"
echo -e "${GREEN}  Phase 1 Complete!${NC}"
echo -e "${GREEN}═══════════════════════════════════════════════${NC}"
echo ""
echo "Backend ready. New endpoints available:"
echo "  • GET  /api/support/my-tickets"
echo "  • GET  /api/support/unread-count"
echo "  • GET  /api/support/tickets/{id}"
echo "  • POST /api/support/tickets/{id}/reply"
echo "  • GET  /api/admin/support/inbox"
echo "  • GET  /api/admin/support/stats"
echo "  • POST /api/admin/support/tickets/{id}/reply"
echo "  • POST /api/admin/support/tickets/{id}/status"
echo "  • POST /api/admin/support/tickets/{id}/assign"
echo "  • POST /api/admin/support/tickets/{id}/priority"
echo ""
echo "Next: run Phase 2 (frontend) to build my-tickets.html + admin-support.html"
echo ""
echo "Monitor logs:"
echo "  sudo journalctl -u runspace.service -f"
echo ""
echo "Rollback if needed:"
echo "  cp $PROGRAM_BACKUP $PROGRAM_CS"
echo "  cp $DB_BACKUP $DB_PATH"
echo "  sudo systemctl restart runspace.service"
