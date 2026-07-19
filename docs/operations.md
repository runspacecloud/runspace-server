# Operations

## Service health

Useful systemd commands:

```bash
sudo systemctl is-active runspace.service
sudo systemctl status runspace.service --no-pager -l
sudo journalctl -u runspace.service -n 200 --no-pager
```

Follow logs in real time:

```bash
sudo journalctl -u runspace.service -f
```

## SQLite backups

Use SQLite's backup command rather than directly copying an active database:

```bash
sudo sqlite3 /path/to/database.db       ".backup '/secure/backup/location/database-backup.db'"
```

Verify the backup:

```bash
sudo sqlite3 /secure/backup/location/database-backup.db       "PRAGMA integrity_check;"
```

The expected result is:

```text
ok
```

## Backup policy

Recommended minimum retention:

- Daily backups for seven days
- Weekly backups for four weeks
- A backup before each deployment
- An encrypted copy outside the production server

A backup stored only on the production server does not protect against
complete server loss.

## Resource monitoring

Regularly monitor:

- Available disk space
- Database size
- Upload storage
- Service memory usage
- Repeated service restarts
- Warning and error logs
