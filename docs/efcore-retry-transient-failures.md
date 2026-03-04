# EF Core Retry on Transient Failures (Npgsql)

This note documents when `EnableRetryOnFailure` retries and how delay is calculated.

## Current Project Settings

From `PostgresPatroniHaproxyEfcoreDemo/Program.cs`:

- `maxRetryCount: 5`
- `maxRetryDelay: TimeSpan.FromSeconds(5)`

## When Retries Happen

With Npgsql EF Core provider, retries happen when `NpgsqlRetryingExecutionStrategy.ShouldRetryOn(...)` evaluates to true:

1. Exception is `NpgsqlException` with `IsTransient == true`
2. Exception is `TimeoutException`
3. Exception is `PostgresException` and its SQLSTATE is in `errorCodesToAdd` (if configured)

Retries do not happen for non-transient errors unless explicitly added via `errorCodesToAdd`.

## Delay Formula

EF Core `ExecutionStrategy.GetNextDelay(...)` uses exponential backoff with jitter:

```text
delayMs = min(
  coefficientMs * (2^k - 1) * (1 + rand * 0.1),
  maxRetryDelayMs
)
```

Where:

- `k = ExceptionsEncountered.Count - 1` (starts at `0`)
- `coefficientMs = 1000` (1 second)
- `rand` is a random number in `[0, 1)`
- Jitter factor is `1 + rand * 0.1`, so it is in `[1.0, 1.1)` (equivalent to `+0%` to `<+10%`)

Important behavior:

- First retry (`k = 0`) has `0ms` delay (immediate retry).

## Delay Sequence for Current Config

With `maxRetryCount = 5` and `maxRetryDelay = 5s`, approximate retry delays are:

1. Retry #1: `0ms`
2. Retry #2: `1000-1100ms`
3. Retry #3: `3000-3300ms`
4. Retry #4: `5000ms` (capped)
5. Retry #5: `5000ms` (capped)

So total wait time from retries alone is about `14.0-14.4s` (excluding query execution time).

## References

- https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.storage.executionstrategy.getnextdelay?view=efcore-10.0
- https://www.npgsql.org/efcore/api/Npgsql.EntityFrameworkCore.PostgreSQL.NpgsqlRetryingExecutionStrategy.html
- https://www.npgsql.org/efcore/api/Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.NpgsqlTransientExceptionDetector.html

## Verification Note

Values/formula above were cross-checked against local runtime assemblies in this repo environment:

- `Microsoft.EntityFrameworkCore` `10.0.3`
- `Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.0`
