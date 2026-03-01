#!/usr/bin/env bash
set -euo pipefail

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

wait_new_leader() {
  local old_leader="$1"
  for _ in {1..45}; do
    local now
    now="$(leader_member || true)"
    if [[ -n "$now" && "$now" != "$old_leader" ]]; then
      echo "$now"
      return 0
    fi
    sleep 2
  done
  return 1
}

wait_streaming_zero_lag() {
  local member="$1"
  for _ in {1..45}; do
    local state_lag
    state_lag="$(cluster | awk -F'|' -v m="$member" '$2 ~ m {gsub(/ /, "", $5); gsub(/ /, "", $7); print $5 "," $7; exit}')"
    echo "[$(date +%H:%M:%S)] $member: ${state_lag:-missing}"
    if [[ "$state_lag" == "streaming,0" ]]; then
      return 0
    fi
    sleep 2
  done
  return 1
}

echo "== BEFORE =="
cluster

old_leader="$(leader_member)"
echo "Old leader: $old_leader"
docker stop "$old_leader" >/dev/null

new_leader="$(wait_new_leader "$old_leader")"
echo "New leader: $new_leader"
echo "== AFTER FAILOVER =="
cluster

if [[ "${SKIP_WRITE_CHECK:-0}" != "1" ]]; then
  echo "Write check via HAProxy :5000"
  if ! curl -fsS -X POST http://localhost:5050/products \
    -H "Content-Type: application/json" \
    -d '{"name":"After failover","price":9.99}'; then
    echo "Write check failed (app API may not be running on :5050)." >&2
  fi
  echo
fi

echo "Rejoin old leader: $old_leader"
docker start "$old_leader" >/dev/null

echo "Wait until rejoined node reaches streaming, lag=0"
wait_streaming_zero_lag "$old_leader" || true

echo "Patroni /replica on rejoined node (expected 200 once healthy):"
docker exec "$old_leader" sh -c 'curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8008/replica'

echo "== FINAL =="
cluster
