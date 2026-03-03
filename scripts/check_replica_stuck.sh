#!/usr/bin/env bash
set -euo pipefail

# Target replica member to inspect (default: patroni1).
member="${1:-patroni1}"

# Return cluster status from any reachable Patroni node.
cluster() {
  for c in patroni1 patroni2 patroni3; do
    local out
    out="$(docker exec "$c" patronictl list 2>/dev/null || true)"
    if [[ -n "$out" ]]; then
      printf '%s\n' "$out"
      return 0
    fi
  done
  echo "No reachable Patroni node for patronictl list" >&2
  return 1
}

leader_member() {
  cluster | awk -F'|' '$4 ~ /Leader/ {gsub(/ /, "", $2); print $2; exit}'
}

leader="$(leader_member)"

# Snapshot key signals from cluster, leader, and target replica.
echo "Leader: $leader"
echo

echo "[cluster]"
cluster
echo

echo "[leader pg_stat_replication]"
docker exec "$leader" psql -U postgres -c \
  "SELECT application_name, state, sync_state, sent_lsn, write_lsn, flush_lsn, replay_lsn FROM pg_stat_replication;"
echo

echo "[replica pg_stat_wal_receiver]"
docker exec "$member" psql -U postgres -c \
  "SELECT status, receive_start_lsn, flushed_lsn, latest_end_lsn, last_msg_send_time, last_msg_receipt_time FROM pg_stat_wal_receiver;"
echo

echo "[replica /replica status code]"
docker exec "$member" sh -c 'curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8008/replica'
