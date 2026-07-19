# Security

## Secret management

Never commit:

- Environment files
- Production credentials
- API tokens
- Database files
- Private encryption keys
- TLS private keys
- TURN credentials
- Billing-provider secrets
- Email-provider credentials
- Cloudflare Turnstile secrets
- Uploaded user data

Production secrets should be provided through protected environment
configuration outside the repository.

## Network boundaries

The intended public request path is:

```text
Internet -> Cloudflare -> Nginx -> ASP.NET Core
```

The backend application port should not be directly exposed to the public
internet.

## Commit review

Before committing:

```bash
git status -sb
git diff --stat
git diff --check
git diff --cached
```

File-name searches are useful but cannot guarantee that source files do not
contain embedded secrets.

## Uploaded media

Uploaded files should be:

- Stored outside the Git repository
- Restricted by size and supported type
- Validated independently of their filename
- Protected against path traversal
- Served using appropriate cache and content headers
- Processed without trusting client-supplied metadata

## Cloudflare

Cloudflare may challenge automated requests such as `curl`.

Diagnose the application and reverse proxy locally before weakening edge
security rules.

Avoid broad security bypasses for the entire API.
