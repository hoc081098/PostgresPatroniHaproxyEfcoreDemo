# PostgreSQL High Availability with Patroni and HAProxy in ASP.NET Core

## Architecture Overview

```
                +---------------------------+
                |  etcd (leader election)   |
                +---------------------------+
                            ↕
     +----------------------+----------------------+----------------------+
     |  pg1 + Patroni       |  pg2 + Patroni       |  pg3 + Patroni       |
     |  (primary hoặc repl) |  (replica)           |  (replica)           |
     +----------------------+----------------------+----------------------+
                            ↑
                      HAProxy
               ┌───────────────┐
               │ pg-write:5000 │ → leader only
               │ pg-read:5001  │ → round robin replicas
               └───────────────┘
                            ↑
                     ASP.NET Core
```