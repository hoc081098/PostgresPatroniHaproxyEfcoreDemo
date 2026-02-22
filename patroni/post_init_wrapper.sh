#!/bin/bash
# Wrapper around Spilo's original /scripts/post_init.sh.
# 1. Runs Spilo's original script first (Zalando extensions, roles, views, etc.)
# 2. Then creates the application user + database for this demo
#    (similar to POSTGRES_DB/POSTGRES_USER in the official postgres image).
#
# Spilo invokes this as: post_init.sh "<HUMAN_ROLE>" "<DATABASE>"
# We forward the same arguments to the original script.

set -e

ORIGINAL_SCRIPT="/scripts/post_init.sh.orig"

# Run Spilo's original post_init if it exists (may be absent in stripped images)
if [ -x "$ORIGINAL_SCRIPT" ]; then
    echo "[post_init] Running Spilo original post_init..."
    "$ORIGINAL_SCRIPT" "$@"
else
    echo "[post_init] No original post_init found at $ORIGINAL_SCRIPT, skipping."
fi

# --- App-specific init (equivalent to POSTGRES_DB / POSTGRES_USER) ---

APP_DB="${APP_DB:-app_db}"
APP_USER="${APP_USER:-app_user}"
APP_PASSWORD="${APP_PASSWORD:-app_pass}"

echo "[post_init] Creating application user '${APP_USER}' and database '${APP_DB}'..."

psql -U "$PGUSER_SUPERUSER" -d postgres <<-EOSQL
    DO \$\$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '${APP_USER}') THEN
            CREATE ROLE ${APP_USER} WITH LOGIN PASSWORD '${APP_PASSWORD}';
            RAISE NOTICE 'Role ${APP_USER} created.';
        ELSE
            RAISE NOTICE 'Role ${APP_USER} already exists, skipping.';
        END IF;
    END
    \$\$;

    SELECT 'CREATE DATABASE ${APP_DB} OWNER ${APP_USER}'
    WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '${APP_DB}')\gexec
EOSQL

echo "[post_init] Done: '${APP_USER}'@'${APP_DB}' is ready."
