#!/bin/sh
# Dumps the live iemas Postgres database to a timestamped, compressed file.
# Intended to be run on the host (via cron) or as a scheduled task, against
# the running iemas-postgres container.
#
# Usage: ./scripts/backup-database.sh [output-directory]
#   Defaults to ./backups relative to the repo root if no directory is given.

set -eu

OUT_DIR="${1:-$(dirname "$0")/../backups}"
CONTAINER="${POSTGRES_CONTAINER:-iemas-postgres}"
DB_USER="${POSTGRES_USER:-iemas}"
DB_NAME="${POSTGRES_DB:-iemas}"
TIMESTAMP=$(date -u +%Y%m%dT%H%M%SZ)
OUT_FILE="$OUT_DIR/iemas-${TIMESTAMP}.dump"

mkdir -p "$OUT_DIR"

echo "Backing up '$DB_NAME' from container '$CONTAINER' to $OUT_FILE ..."
docker exec "$CONTAINER" pg_dump -U "$DB_USER" -d "$DB_NAME" --format=custom --compress=9 > "$OUT_FILE"

SIZE=$(wc -c < "$OUT_FILE" | tr -d ' ')
if [ "$SIZE" -lt 1024 ]; then
  echo "WARNING: backup file is suspiciously small ($SIZE bytes) — check for errors above." >&2
  exit 1
fi

echo "Backup complete: $OUT_FILE ($SIZE bytes)."
echo "Verify with: ./scripts/restore-database.sh $OUT_FILE --dry-run"
