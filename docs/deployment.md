# Deployment

## Pre-deployment checks

Review the repository state:

```bash
git status -sb
git log --oneline -5
git diff --check
```

Build the backend:

```bash
cd backend
dotnet restore
dotnet build -c Release --no-restore
```

JavaScript files should also be checked when Node.js is available:

```bash
node --check frontend/chatt/friend-requests-sidebar.js
node --check frontend/chatt/friends-modal.js
```

## Recommended deployment sequence

1. Create a verified database backup.
2. Back up the currently deployed application.
3. Build and publish the new backend release.
4. Stop the backend service.
5. Replace the deployed backend files.
6. Verify file ownership and permissions.
7. Start the service.
8. Validate Nginx configuration.
9. Run local health checks.
10. Inspect application logs.

## Health checks

Test the backend directly from the server:

```bash
curl -fsS http://127.0.0.1:5000/api/ping
```

Test through the local reverse proxy using the appropriate hostname and
TLS configuration.

A public command-line request may receive a Cloudflare challenge even when
both the backend and reverse proxy are healthy.

## Rollback

A rollback should restore the previous application release before the
service is restarted.

Database backups should only be restored when application rollback alone
is insufficient.
