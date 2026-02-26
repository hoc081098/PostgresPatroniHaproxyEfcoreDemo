# PostgreSQL High Availability with Patroni, HAProxy & EF Core

## Architecture Overview

```
                         ┌──────────────────────────────────────────┐
                         │         etcd cluster (3 nodes)           │
                         │  etcd1 : etcd2 : etcd3  (port 2379/2380) │
                         │      Raft consensus / leader election    │
                         └───────────┬──────────────────────────────┘
                                     │ ↕ heartbeat & lock
              ┌──────────────────────┼───────────────────────┐
              │                      │                       │
   ┌──────────▼──────────┐ ┌─────────▼───────────┐ ┌─────────▼───────────┐
   │  patroni1 (Spilo)   │ │  patroni2 (Spilo)   │ │  patroni3 (Spilo)   │
   │  PostgreSQL :5432   │ │  PostgreSQL :5432   │ │  PostgreSQL :5432   │
   │  REST API    :8008  │ │  REST API    :8008  │ │  REST API    :8008  │
   │  (primary or repl)  │ │  (replica)          │ │  (replica)          │
   └─────────────────────┘ └─────────────────────┘ └─────────────────────┘
              │                      │                       │
              └──────────────────────┼───────────────────────┘
                                     │
                          ┌──────────▼──────────┐
                          │       HAProxy       │
                          │  :5000 → primary    │  GET /primary  → 200 on leader only
                          │  :5001 → replicas   │  GET /replica  → 200 on replicas only
                          │  :8404 → stats UI   │      round-robin across healthy replicas
                          └──────────┬──────────┘
                                     │
                          ┌──────────▼───────────┐
                          │    ASP.NET Core      │
                          │  WriteDb → :5000     │
                          │  ReadDb  → :5001     │
                          └──────────────────────┘
```

## What Is Implemented

| Component                  | Detail                                                                                                                                                   |
|----------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------|
| **etcd cluster**           | 3-node etcd (v3.5) for Patroni leader election via Raft consensus                                                                                        |
| **Patroni cluster**        | 3 Spilo nodes (Patroni + PostgreSQL 17 + WAL-G), automatic primary election & failover                                                                   |
| **post_init_wrapper.sh**   | Runs Spilo's original `post_init.sh` first, then creates `app_user` + `app_db` (similar to `POSTGRES_DB`/`POSTGRES_USER` in the official postgres image) |
| **HAProxy**                | Single HAProxy instance; port 5000 → primary (write), port 5001 → replicas (read, round-robin), port 8404 → stats UI                                     |
| **Health checks**          | HAProxy uses Patroni REST API (`GET /primary`, `GET /replica`) over HTTP on port 8008 while PostgreSQL traffic stays TCP                                 |
| **ASP.NET Core + EF Core** | Two `DbContext`s — `ApplicationWriteDbContext` (→ HAProxy :5000) and `ApplicationReadDbContext` (→ HAProxy :5001)                                        |

## How to Run

```bash
docker compose up -d
```

Check cluster status:

```bash
# Patroni cluster status
docker exec -it patroni1 patronictl -c /home/postgres/postgres.yml list

# HAProxy stats (browser)
open http://localhost:8404   # credentials: haproxy / haproxy

# Which node is primary right now?
docker exec -it patroni1 patronictl -c /home/postgres/postgres.yml list | grep Leader
```

Test connectivity via the ASP.NET Core API:

```bash
curl http://localhost:5050/
```

---

## What Can / Should Be Demoed Next

### 🔁 Failover & Recovery

- [ ] **Manual failover** — `patronictl switchover` and observe HAProxy rerouting write traffic to the new primary in
  real time (watch the stats page at `:8404`)
- [ ] **Kill the primary** — `docker stop patroni1` → Patroni elects a new leader, HAProxy health checks detect it
  automatically, EF Core write queries resume without any code change
- [ ] **Rejoin a node** — `docker start patroni1` → node rejoins as replica, HAProxy adds it back to the read pool

### 📊 Read Load Balancing

- [ ] **Demonstrate round-robin reads** — send many `GET /` requests and log which PostgreSQL backend each query lands
  on (add `pg_backend_pid()` or `inet_server_addr()` to the read endpoint)
- [ ] **Simulate a replica lag** — add `pg_sleep()` on one replica and watch HAProxy's health check eventually pull it
  out of rotation

### 🏗️ EF Core & Database Migrations

- [ ] **Run EF Core migrations** — create a real entity (e.g., `Product`), apply migration only through the write
  connection, verify replication to replicas
- [ ] **Read-your-writes concern** — demonstrate the edge case where a write followed immediately by a read on a replica
  may not see the freshest data (replication lag)

### 🔒 Security Hardening (non-superuser app user)

- [ ] **Verify the app never uses `postgres` superuser** — show that `app_user` only has `CONNECT` + `USAGE` + DML on
  `app_db`, not superuser privileges (follows Patroni docs recommendation)
- [ ] **Grant least-privilege** — add explicit `GRANT` statements in `post_init_wrapper.sh` for specific schemas/tables

### 🐛 Observability & Debugging

- [ ] **HAProxy stats deep-dive** — explain each column (current sessions, bytes in/out, health check status, last
  change) on the `:8404` stats page
- [ ] **Patroni REST API tour** — `curl http://localhost:8008/primary`, `/replica`, `/health`, `/patroni`, `/cluster`
  directly against each node
- [ ] **etcd data inspection** — `etcdctl get --prefix /service/postgres-ha` to see the DCS keys Patroni writes (leader
  lock, member info, config)

### 🌐 Connection Pooling (next layer)

- [ ] **Add PgBouncer** — sit PgBouncer between HAProxy and the app; compare connection count with and without pooling
  under load
- [ ] **Transaction-mode pooling** — show how it interacts with EF Core (session-level features like temp tables,
  `SET LOCAL`, advisory locks won't work)

### 🏋️ Load Testing

- [ ] **k6 / wrk / pgbench** — generate concurrent read + write load; watch HAProxy stats, PostgreSQL
  `pg_stat_activity`, and replication lag in real time
- [ ] **Chaos test** — combine failover + load test; verify p99 latency and error rate during the election window

### ☁️ Production Patterns (stretch goals)

- [ ] **WAL-G backup & restore** — Spilo ships with WAL-G; configure an S3-compatible bucket (MinIO) and demo PITR (
  Point-In-Time Recovery)
- [ ] **Multiple HAProxy instances + Keepalived** — add a second HAProxy and a virtual IP (VIP) to eliminate the HAProxy
  single point of failure
- [ ] **Kubernetes / Helm** — migrate the same topology to k8s using
  the [Zalando Postgres Operator](https://github.com/zalando/postgres-operator)
