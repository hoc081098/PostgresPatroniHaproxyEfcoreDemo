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

---

## What Is Implemented

| Component                  | Detail                                                                                                                                                   |
|----------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------|
| **etcd cluster**           | 3-node etcd (v3.5) for Patroni leader election via Raft consensus                                                                                        |
| **Patroni cluster**        | 3 Spilo nodes (Patroni + PostgreSQL 17 + WAL-G), automatic primary election & failover                                                                   |
| **post_init_wrapper.sh**   | Runs Spilo's original `post_init.sh` first, then creates `app_user` + `app_db` (similar to `POSTGRES_DB`/`POSTGRES_USER` in the official postgres image) |
| **HAProxy**                | Single HAProxy instance; port 5000 → primary (write), port 5001 → replicas (read, round-robin), port 8404 → stats UI                                     |
| **Health checks**          | HAProxy uses Patroni REST API (`GET /primary`, `GET /replica`) over HTTP on port 8008 while PostgreSQL traffic stays TCP                                 |
| **ASP.NET Core + EF Core** | Two `DbContext`s — `ApplicationWriteDbContext` (→ HAProxy :5000) and `ApplicationReadDbContext` (→ HAProxy :5001)                                        |

---

## How to Run

```bash
docker compose up -d

# Patroni cluster status
# Note: no -c flag needed — Spilo pre-sets PATRONICTL_CONFIG_FILE env var inside the container
docker exec -it patroni1 patronictl list

# HAProxy stats (browser) — credentials: haproxy / haproxy
open http://localhost:8404
```

---

## Demo Scenarios

### ✅ Manual Switchover (done)

A **switchover** is a graceful handover — the primary finishes in-flight transactions before stepping down.
Use `failover` only when the primary is already dead.

> **Note:** When using Spilo, the `--leader` value is the **container ID** shown in the `Member` column of
> `patronictl list` (e.g. `8366a0cc9c0b`), not the hostname `patroni1`.
> `--master` was deprecated in Patroni v3.x.

```bash
# 1. Check current cluster state — note the Member name of the Leader
docker exec -it patroni1 patronictl list

# 2. Switchover — replace <leader-member-name> with the Member name from step 1
#    Omit --candidate to let Patroni pick the most up-to-date replica automatically
docker exec -it patroni1 patronictl switchover \
  --leader <leader-member-name> \
  --scheduled now \
  --force

# 3. Verify the new primary
docker exec -it patroni1 patronictl list
```

**Terminal output:**

```
hoc.nguyen@MBAM0187 % docker exec -it patroni1 patronictl list
+ Cluster: postgres-ha (7609605213415538750) -----+----+-----------+
| Member       | Host       | Role    | State     | TL | Lag in MB |
+--------------+------------+---------+-----------+----+-----------+
| 8366a0cc9c0b | 172.21.0.5 | Leader  | running   |  6 |           |
| a83e997af22c | 172.21.0.6 | Replica | streaming |  6 |         0 |
| da0b3dc3cce5 | 172.21.0.7 | Replica | streaming |  6 |         0 |
+--------------+------------+---------+-----------+----+-----------+

hoc.nguyen@MBAM0187 % docker exec -it patroni1 patronictl switchover \
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

hoc.nguyen@MBAM0187 % docker exec -it patroni1 patronictl list
+ Cluster: postgres-ha (7609605213415538750) -----+----+-----------+
| Member       | Host       | Role    | State     | TL | Lag in MB |
+--------------+------------+---------+-----------+----+-----------+
| 8366a0cc9c0b | 172.21.0.5 | Replica | streaming |  7 |         0 |
| a83e997af22c | 172.21.0.6 | Replica | streaming |  7 |         0 |
| da0b3dc3cce5 | 172.21.0.7 | Leader  | running   |  7 |           |
+--------------+------------+---------+-----------+----+-----------+
```

**HAProxy stats — before & after:**

<img src="./images/img_manual_failover_before.png" alt="HAProxy stats before switchover" height="500">

> **Before** — Leader: `patroni3` (`8366a0cc9c0b`) · `patroni1` & `patroni2` are replicas

<img src="./images/img_manual_failover_after.png" alt="HAProxy stats after switchover" height="500">

> **After** — Leader: `patroni1` (`da0b3dc3cce5`) · `patroni2` & `patroni3` are replicas

---

### 🔁 Automatic Failover (kill the primary)

- [ ] `docker stop patroni1` → Patroni elects a new leader automatically
- [ ] HAProxy detects the change via health checks within ~3–9s (`inter 3s fall 3`)
- [ ] EF Core write queries resume without any code change
- [ ] `docker start patroni1` → node rejoins as replica, HAProxy adds it back to the read pool

### 📊 Read Load Balancing

- [ ] Send many `GET /` requests and log which PostgreSQL backend each query lands on — add `inet_server_addr()` or `pg_backend_pid()` to the read endpoint to verify round-robin is working
- [ ] Simulate replica lag with `pg_sleep()` on one replica and watch HAProxy pull it out of rotation

### 🏗️ EF Core Migrations

- [ ] Create a real entity (e.g. `Product`), apply migration only through the write connection (`:5000`)
- [ ] Verify the schema is replicated to all replicas automatically
- [ ] Demonstrate **read-your-writes** edge case: a write followed immediately by a read on a replica may not see the freshest data due to replication lag

### 🔒 Security — Non-superuser App User

- [ ] Verify the app never uses the `postgres` superuser — `app_user` should only have `CONNECT` + `USAGE` + DML on `app_db` (follows [Patroni docs recommendation](https://patroni.readthedocs.io/en/latest/security.html))
- [ ] Add explicit `GRANT` statements in `post_init_wrapper.sh` scoped to specific schemas/tables

### 🐛 Observability & Debugging

- [x] **HAProxy stats page** (`:8404`) — walk through important columns to understand backend health and load distribution:
  - `Status` column is the most important. It shows the current state of a backend server:
    - `UP` means the backend is healthy and receiving traffic
    - `DOWN` means it's unhealthy and traffic is not routed to this server.
  - `LastChk` displays the result of the most recent health check.
    - `	L7OK/200 in 3ms`: Layer 7 check succeeded, HTTP 200 response, took 3 milliseconds.
    - `	L7STS/503 in 5ms`: Layer 7 check failed, HTTP 503 response, took 5 milliseconds. HAProxy considers this a failure.
  - `Wght` defines the load balancing weight of the server. If multiple servers have the same weight, traffic is distributed evenly (e.g., round-robin).
    If weights differ, traffic is distributed proportionally.
  - `Act / Bck`:
    - `Act` shows whether this server is configured as an **active** server in the backend.
      It displays `Y` if the server is active (receives traffic under normal conditions),
      and `-` if not.
    - `Bck` shows whether this server is configured as a **backup** server.
      It displays `Y` if the server is marked as `backup` in HAProxy configuration.
      Backup servers are only used when all active servers are down.
  - `Chk / Dwn / Dwntime`:
    - `Chk` indicates the number of consecutive failed health checks.
      If this count exceeds the configured threshold (`fall` parameter), HAProxy marks the server as `DOWN`. This is useful to understand whether a server flapped or genuinely failed.
    - `Dwn` indicates the total number of times the server has been marked as `DOWN`.
    - `Dwntme` shows the total accumulated time the server has been in `DOWN` state.
    - These columns are extremely useful for understanding failover frequency and stability issues.
  - `Sessions`:
    - `Cur` shows the current number of active sessions on that backend server.
    - `Max` shows the maximum number of sessions that have been active at the same time since HAProxy started.
    - `Total` shows the total number of sessions that have been handled by that server since HAProxy started.
    - If one backend node consistently shows higher `Cur` values, it may indicate uneven load balancing.
  - `Bytes`:
    - `In` shows the total number of bytes received from clients for that backend server.
    - `Out` shows the total number of bytes sent to clients from that backend server.
    - If one server has significantly higher `Bytes In` or `Bytes Out`, it may indicate it's handling more traffic than others, which could be a sign of load imbalance or a hotspot.

- [ ] **Patroni REST API** — `curl` directly against each node: `/primary`, `/replica`, `/health`, `/patroni`, `/cluster`
- [ ] **etcd inspection** — `etcdctl get --prefix /service/postgres-ha` to see the DCS keys Patroni writes (leader lock, member info, config)

### 🌐 Connection Pooling

- [ ] Add **PgBouncer** between the app and HAProxy; compare connection count with and without pooling under load
- [ ] Show how **transaction-mode pooling** interacts with EF Core — session-level features (`SET LOCAL`, temp tables, advisory locks) won't work in this mode

### 🏋️ Load Testing

- [ ] **k6 / pgbench** — concurrent read + write load; observe HAProxy stats, `pg_stat_activity`, replication lag in real time
- [ ] **Chaos test** — combine failover + load test; measure p99 latency and error rate during the election window

### ☁️ Production Patterns (stretch goals)

- [ ] **WAL-G + MinIO** — configure S3-compatible backup, demo PITR (Point-In-Time Recovery)
- [ ] **HA HAProxy** — add a second HAProxy + Keepalived VIP to remove HAProxy as a single point of failure
- [ ] **Kubernetes** — migrate to k8s using the [Zalando Postgres Operator](https://github.com/zalando/postgres-operator)
