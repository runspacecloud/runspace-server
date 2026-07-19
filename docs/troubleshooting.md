# Troubleshooting

## Backend fails to start

Inspect the service:

```bash
sudo systemctl status runspace.service --no-pager -l
sudo journalctl -u runspace.service -n 200 --no-pager
```

Confirm that the backend port is listening:

```bash
sudo ss -lntp
```

## Local API works but the public API fails

Test the layers separately:

1. ASP.NET Core directly
2. Nginx locally
3. The public Cloudflare hostname

Interpretation:

- Backend failure indicates an application or middleware problem.
- Backend success with Nginx failure indicates a proxy configuration issue.
- Backend and Nginx success with public failure indicates an edge, DNS or
  Cloudflare issue.

A response header containing:

```text
cf-mitigated: challenge
```

means Cloudflare issued a browser challenge.

## Build warnings

Review compiler warnings even when the build succeeds.

Important warning categories include:

- Possible null-value conversions
- Async methods without awaited operations
- Unused exception variables
- Stack allocation inside loops

## Database problems

Run an integrity check:

```bash
sqlite3 /path/to/database.db "PRAGMA integrity_check;"
```

Create a backup before attempting destructive repairs.

## WebSocket and SignalR problems

Verify that:

- WebSocket upgrade headers are forwarded
- The backend service is running
- Proxy timeouts are suitable
- Cloudflare WebSockets are enabled
- Client and server routes match

## Voice-call problems

If signaling succeeds but voice does not connect:

- Inspect WebRTC ICE states
- Verify STUN configuration
- Confirm TURN availability
- Check UDP and TCP firewall rules
- Confirm credentials are supplied securely
