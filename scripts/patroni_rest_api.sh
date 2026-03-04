#!/usr/bin/env bash
set -euo pipefail

# Target Patroni node to query for REST API (default: patroni1).
patroni_node="${1:-patroni1}"

print_separator() {
  echo
  echo "--------------------------------"
}

echo "== Patroni REST API Demo =="

echo "GET /primary: 200 means this node is primary, 503 means it's not"
docker exec "$patroni_node" sh -c 'curl -s -w "\nStatus code: %{http_code}\n" http://127.0.0.1:8008/primary'
print_separator

echo "GET /replica: 200 means this node is replica, 503 means it's not"
docker exec "$patroni_node" sh -c 'curl -s -w "\nStatus code: %{http_code}\n" http://127.0.0.1:8008/replica'
print_separator

echo "GET /health: 200 means healthy (not necessarily primary), 503 means unhealthy"
docker exec "$patroni_node" sh -c 'curl -s -w "\nStatus code: %{http_code}\n" http://127.0.0.1:8008/health'
print_separator

echo "GET /patroni: local node status and config (JSON)"
docker exec "$patroni_node" sh -c 'curl -s -w "\nStatus code: %{http_code}\n" http://127.0.0.1:8008/patroni'

echo "GET /cluster: cluster-wide status and member list"
docker exec "$patroni_node" sh -c 'curl -s -w "\nStatus code: %{http_code}\n" http://127.0.0.1:8008/cluster'



