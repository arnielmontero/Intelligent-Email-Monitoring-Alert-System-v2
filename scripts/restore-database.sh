#!/bin/sh
# Restores a backup produced by backup-database.sh into a DISPOSABLE, isolated
# Postgres container for verification — never directly into the live
# iemas-postgres container, so a bad backup or a restore mistake can never
# damage live data. This matches the drill methodology used in Phase 10's
# backup/restore verification.
#
# Usage:
#   ./scripts/restore-database.sh <backup-file> --dry-run
#     Restores into a throwaway container, runs a few sanity queries, tears
#     the container down. Never touches the live database.
#
#   ./scripts/restore-database.sh <backup-file> --apply-to-live
#     Restores into the REAL iemas-postgres container. Destructive — prompts
#     for explicit confirmation. Only use this for an actual disaster
#     recovery, never routinely.

set -eu

BACKUP_FILE="${1:?Usage: $0 <backup-file> --dry-run|--apply-to-live}"
MODE="${2:?Usage: $0 <backup-file> --dry-run|--apply-to-live}"

if [ ! -f "$BACKUP_FILE" ]; then
  echo "Backup file not found: $BACKUP_FILE" >&2
  exit 1
fi

DB_USER="${POSTGRES_USER:-iemas}"
DB_NAME="${POSTGRES_DB:-iemas}"
DB_PASSWORD="${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD to the value matching the backup's source database}"
NETWORK="${DOCKER_NETWORK:-intelligent-email-monitoring-alert-system-v2_default}"

case "$MODE" in
  --dry-run)
    TEST_CONTAINER="iemas-restore-drill-$(date +%s)"
    echo "Starting disposable Postgres container '$TEST_CONTAINER' for restore verification..."
    docker run -d --name "$TEST_CONTAINER" --network "$NETWORK" \
      -e POSTGRES_DB="$DB_NAME" -e POSTGRES_USER="$DB_USER" -e POSTGRES_PASSWORD="$DB_PASSWORD" \
      postgres:16-alpine >/dev/null

    cleanup() {
      echo "Tearing down '$TEST_CONTAINER'..."
      docker rm -f "$TEST_CONTAINER" >/dev/null 2>&1 || true
    }
    trap cleanup EXIT

    echo "Waiting for the disposable instance to accept connections..."
    for i in $(seq 1 30); do
      if docker exec "$TEST_CONTAINER" pg_isready -U "$DB_USER" -d "$DB_NAME" >/dev/null 2>&1; then
        break
      fi
      sleep 1
    done

    echo "Restoring backup into '$TEST_CONTAINER'..."
    docker exec -i "$TEST_CONTAINER" pg_restore -U "$DB_USER" -d "$DB_NAME" --no-owner < "$BACKUP_FILE"

    echo "Running sanity checks..."
    TABLE_COUNT=$(docker exec "$TEST_CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" -t -c \
      "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public';" | tr -d ' ')
    MIGRATION_COUNT=$(docker exec "$TEST_CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" -t -c \
      "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";" | tr -d ' ')

    echo "Restored schema has $TABLE_COUNT table(s) and $MIGRATION_COUNT applied migration(s)."
    if [ "$TABLE_COUNT" -lt 30 ] || [ "$MIGRATION_COUNT" -lt 1 ]; then
      echo "WARNING: restored schema looks incomplete — investigate before trusting this backup." >&2
      exit 1
    fi
    echo "Dry-run restore verification PASSED. The live database was never touched."
    ;;

  --apply-to-live)
    echo "!! THIS WILL OVERWRITE THE LIVE 'iemas-postgres' DATABASE !!"
    echo "Type the database name ($DB_NAME) to confirm, or anything else to abort:"
    read -r CONFIRM
    if [ "$CONFIRM" != "$DB_NAME" ]; then
      echo "Confirmation did not match. Aborted, no changes made."
      exit 1
    fi
    echo "Restoring into live iemas-postgres (dropping and recreating the target database)..."
    docker exec iemas-postgres psql -U "$DB_USER" -d postgres -c "DROP DATABASE IF EXISTS \"${DB_NAME}_restoring\";"
    docker exec iemas-postgres psql -U "$DB_USER" -d postgres -c "CREATE DATABASE \"${DB_NAME}_restoring\";"
    docker exec -i iemas-postgres pg_restore -U "$DB_USER" -d "${DB_NAME}_restoring" --no-owner < "$BACKUP_FILE"
    echo "Restored into a side database '${DB_NAME}_restoring' for a final check before cutover."
    echo "Once verified, coordinate the actual cutover (rename databases, restart the API) manually —"
    echo "this script deliberately stops short of an automatic swap of the live database."
    ;;

  *)
    echo "Unknown mode: $MODE (expected --dry-run or --apply-to-live)" >&2
    exit 1
    ;;
esac
