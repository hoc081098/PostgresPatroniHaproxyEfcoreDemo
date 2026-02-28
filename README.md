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
# Note: no -c flag needed — Spilo pre-sets PATRONICTL_CONFIG_FILE env var inside the container
docker exec -it patroni1 patronictl list

# HAProxy stats (browser)
open http://localhost:8404   # credentials: haproxy / haproxy

# Which node is primary right now?
docker exec -it patroni1 patronictl list | grep Leader
```

Test connectivity via the ASP.NET Core API:

```bash
curl http://localhost:5050/
```

---

## What Can / Should Be Demoed Next

### 🔁 Failover & Recovery

#### Manual Failover ✅

- `docker exec -it patroni1 patronictl switchover --leader <leader> --scheduled now --force` and observe HAProxy rerouting write traffic to the new primary in real time (watch the stats page at `:8404`)

  - `switchover`: graceful handover — primary finishes in-flight transactions before stepping down (vs `failover` which is used when the primary is already dead)
  - `--leader <leader>`: the current primary **Member name** from `patronictl list` (when using Spilo, this is the container ID e.g. `8366a0cc9c0b`, not the hostname `patroni1`); `--master` was deprecated in Patroni v3.x
  - `--candidate <replica>`: which replica to promote; omit to let Patroni pick the most up-to-date one automatically
  - `--scheduled now`: run immediately instead of scheduling for a future time
  - `--force`: skip the interactive `y/N` confirmation prompt

###### Script for manual switchover:

```bash
# 1. Check who is currently the primary — note the Member name (container ID when using Spilo)
docker exec -it patroni1 patronictl list

# 2. Switchover — replace <leader-member-name> with the actual Member name from step 1
#    e.g. --leader 8366a0cc9c0b  (omit --candidate to let Patroni choose automatically)
docker exec -it patroni1 patronictl switchover \
  --leader <leader-member-name> \
  --scheduled now \
  --force

# 3. Verify the new primary after switchover
docker exec -it patroni1 patronictl list
```

###### The result (terminal output):

```terminaloutput
hoc.nguyen@MBAM0187 PostgresPatroniHaproxyEfcoreDemo % docker exec -it patroni1 patronictl list
+ Cluster: postgres-ha (7609605213415538750) -----+----+-----------+
| Member       | Host       | Role    | State     | TL | Lag in MB |
+--------------+------------+---------+-----------+----+-----------+
| 8366a0cc9c0b | 172.21.0.5 | Leader  | running   |  6 |           |
| a83e997af22c | 172.21.0.6 | Replica | streaming |  6 |         0 |
| da0b3dc3cce5 | 172.21.0.7 | Replica | streaming |  6 |         0 |
+--------------+------------+---------+-----------+----+-----------+


hoc.nguyen@MBAM0187 PostgresPatroniHaproxyEfcoreDemo % docker exec -it patroni1 patronictl switchover \
  --leader 8366a0cc9c0b \ 
  --scheduled now \
  --force
Current cluster topology
+ Cluster: postgres-ha (7609605213415538750) -----+----+-----------+
| Member       | Host       | Role    | State     | TL | Lag in MB |
+--------------+------------+---------+-----------+----+-----------+
| 8366a0cc9c0b | 172.21.0.5 | Leader  | running   |  6 |           |
| a83e997af22c | 172.21.0.6 | Replica | streaming |  6 |         0 |
| da0b3dc3cce5 | 172.21.0.7 | Replica | streaming |  6 |         0 |
+--------------+------------+---------+-----------+----+-----------+
2026-02-28 07:41:29.48608 Successfully switched over to "da0b3dc3cce5"
+ Cluster: postgres-ha (7609605213415538750) ---+----+-----------+
| Member       | Host       | Role    | State   | TL | Lag in MB |
+--------------+------------+---------+---------+----+-----------+
| 8366a0cc9c0b | 172.21.0.5 | Replica | stopped |    |   unknown |
| a83e997af22c | 172.21.0.6 | Replica | running |  6 |         0 |
| da0b3dc3cce5 | 172.21.0.7 | Leader  | running |  7 |           |
+--------------+------------+---------+---------+----+-----------+


hoc.nguyen@MBAM0187 PostgresPatroniHaproxyEfcoreDemo % docker exec -it patroni1 patronictl list
+ Cluster: postgres-ha (7609605213415538750) -----+----+-----------+
| Member       | Host       | Role    | State     | TL | Lag in MB |
+--------------+------------+---------+-----------+----+-----------+
| 8366a0cc9c0b | 172.21.0.5 | Replica | streaming |  7 |         0 |
| a83e997af22c | 172.21.0.6 | Replica | streaming |  7 |         0 |
| da0b3dc3cce5 | 172.21.0.7 | Leader  | running   |  7 |           |
+--------------+------------+---------+-----------+----+-----------+
```

###### The result (screenshot from HAProxy stats page):

<img src="./images/img_manual_failover_before.png" alt="HAProxy stats before switchover" height="500">

> **Before switchover** — Leader: `patroni3` (`8366a0cc9c0b`) · `patroni1` & `patroni2` are replicas

<br>

<img src="./images/img_manual_failover_after.png" alt="HAProxy stats after switchover" height="500">

> **After switchover** — Leader: `patroni1` (`da0b3dc3cce5`) · `patroni2` & `patroni3` are replicas

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
