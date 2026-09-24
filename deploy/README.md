# Production deployment (single host, Docker Compose)

This overlay adds a reverse proxy (Caddy, automatic HTTPS) and a production
build of the CMS in front of the existing `postgres`/`api` services, without
changing how local development works (`docker-compose.yml` alone is
untouched and still the right way to run this locally).

## What's new

- `web-cms/Dockerfile` + `web-cms/nginx.conf` — builds the CMS as a static
  production bundle (`npm run build`) and serves it via nginx.
- `deploy/Caddyfile` — reverse proxy: routes `/api/*` and `/hubs/*` to the API
  container, everything else to the CMS. Automatic Let's Encrypt HTTPS when
  `DOMAIN` is a real, publicly resolvable hostname; a plain-HTTP `:80`
  fallback block for domain-less/IP-only access otherwise.
- `docker-compose.prod.yml` — overlay (not a replacement) that removes direct
  host port exposure from `postgres`/`api` (Caddy becomes the only public
  entry point) and adds the `web` and `proxy` services.
- `server/src/Iemas.Api/Program.cs` now calls `db.Database.MigrateAsync()` on
  startup — a fresh deployment no longer needs a manual
  `dotnet ef database update` before the API can serve traffic. This is
  idempotent (a no-op if the schema is already current), so it's safe on an
  existing deployment too.
- `scripts/backup-database.sh` / `scripts/restore-database.sh` — see
  "Backup and recovery" below.

## Deploying

1. Copy `.env.example` to `.env` and fill in real values — especially
   `DOMAIN` (a real DNS A/AAAA record pointing at this host, if you want
   automatic HTTPS) and the three secrets. **Do not reuse the values
   currently committed in `appsettings.Development.json`** — those are
   tracked in Finding #1 as compromised and are being rotated separately;
   generate fresh ones (`openssl rand -base64 32` / `48`).

2. Bring up the full stack:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
   ```

3. Verify:

   ```bash
   curl https://$DOMAIN/health           # CMS is served
   curl https://$DOMAIN/api/v1/auth/login -X POST -H "Content-Type: application/json" -d '{}'
                                          # expect 400 (reached the API, bad body)
   ```

   If `DOMAIN` isn't set (or isn't publicly resolvable yet), use
   `http://<host-ip>/` instead — Caddy's `:80` fallback block serves both
   routes without TLS in that case.

## Known local-dev-machine caveat (not a production issue)

If you run this on a Windows machine that already has something else bound
to ports 80/443 (e.g. Laragon's Apache, IIS, or another reverse proxy), that
existing process — not Docker — will win the port and this stack's `/health`
and `/api/*` routes will appear to 404 from `curl localhost`, even though
Caddy and the containers are running correctly. This was hit and confirmed
during this phase's own verification: `docker port iemas-proxy` showed the
correct bindings, but a process (`httpd.exe`, Laragon) already held
`0.0.0.0:80`. Verified past this by curling the containers directly over the
Docker network instead of through the host port — that confirmed Caddy's
routing and the CMS/API responses are both correct; the conflict is a
property of this specific dev machine, not the deployment configuration. A
real, dedicated production host without a competing web server on 80/443
will not have this problem.

## Backup and recovery

```bash
# Take a backup (writes to ./backups/, gitignored — never commit these)
./scripts/backup-database.sh

# Verify a backup restores cleanly, WITHOUT touching the live database —
# spins up a disposable Postgres container, restores into it, checks table/
# migration counts, tears it down automatically
POSTGRES_PASSWORD=<value from .env> ./scripts/restore-database.sh backups/iemas-<timestamp>.dump --dry-run

# Actual disaster recovery only — restores into a side database on the real
# iemas-postgres container for a final check before manual cutover. Requires
# typing the database name to confirm; never runs automatically.
POSTGRES_PASSWORD=<value from .env> ./scripts/restore-database.sh backups/iemas-<timestamp>.dump --apply-to-live
```

Both scripts were run against this project's real running stack during Phase
12 verification: a live backup (227KB, real data) was taken and successfully
dry-run-restored into a disposable container (34 tables, 10 migrations
confirmed), with the live database confirmed unaffected afterward.

Schedule `backup-database.sh` via cron/Task Scheduler for routine backups —
this repo does not do that automatically, since the right schedule and
retention policy are an operational decision for whoever runs this in
production, not something to hardcode here.

## Known gap, not addressed by this phase

Employee `fullName` (and possibly other free-text fields) accepts
stored-XSS-shaped input without server-side sanitization on write — tracked
in `IEMAS_Build_Progress_Tracker.md` under Known Gaps, deliberately not
fixed as part of this deployment phase so the existing Phase 11 test result
stays independently verifiable. Address it as its own tracked follow-up.

Finding #1 (real secrets committed to `appsettings.Development.json` and
Git history) is tracked and remediation is prepared (`scripts/
rotate-credential-key.js`, `scripts/FINDING-1-REMEDIATION.md`) but requires
manual execution outside of an automated coding session, since it involves
handling real cryptographic key material end-to-end and a destructive
history-rewriting force-push — see that runbook for the exact steps.
