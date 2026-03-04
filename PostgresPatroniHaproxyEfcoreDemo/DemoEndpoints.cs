using Microsoft.EntityFrameworkCore;
using PostgresPatroniHaproxyEfcoreDemo.Data;
using PostgresPatroniHaproxyEfcoreDemo.Data.Entities;

namespace PostgresPatroniHaproxyEfcoreDemo;

public sealed record ProductRequest(string Name, decimal Price);

public static class DemoEndpoints
{
    extension(IEndpointRouteBuilder endpoints)
    {
        public void MapDemoEndpoints()
        {
            // GET / — health check: verify connectivity to both write and read DB via HAProxy
            endpoints.MapGet("/",
                (ApplicationReadDbContext readDb, ApplicationWriteDbContext writeDb) =>
                {
                    var canConnectToReadDb = readDb.Database.CanConnect();
                    var canConnectToWriteDb = writeDb.Database.CanConnect();

                    return new
                    {
                        CanConnectToReadDb = canConnectToReadDb,
                        CanConnectToWriteDb = canConnectToWriteDb
                    };
                });

            // POST /products — write through HAProxy :5000 → primary node
            endpoints.MapPost("/products",
                async (ProductRequest request, ApplicationWriteDbContext writeDb,
                    CancellationToken cancellationToken) =>
                {
                    var product = Product.Create(request.Name, request.Price, DateTimeOffset.UtcNow);

                    writeDb.Products.Add(product);
                    await writeDb.SaveChangesAsync(cancellationToken);

                    return Results.Created($"/products/{product.Id}", product);
                });

            // POST /products/read-your-writes-demo — write to primary, then immediately read
            // from replica (:5001) and primary (:5000) to demonstrate potential stale reads.
            endpoints.MapPost("/products/read-your-writes-demo",
                async (ProductRequest request, ApplicationReadDbContext readDb, ApplicationWriteDbContext writeDb,
                    CancellationToken cancellationToken) =>
                {
                    var product = Product.Create(request.Name, request.Price, DateTimeOffset.UtcNow);

                    writeDb.Products.Add(product);
                    await writeDb.SaveChangesAsync(cancellationToken);

                    // Clear tracked entities so the next primary read is fetched from DB, not from change tracker memory.
                    writeDb.ChangeTracker.Clear();

                    var productFromReplica = await readDb.Products
                        .Where(p => p.Id == product.Id)
                        .Select(p => new
                        {
                            p.Id,
                            p.Name,
                            p.Price,
                            p.CreatedAtUtc
                        })
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken);

                    var productFromPrimary = await writeDb.Products
                        .AsNoTracking()
                        .Where(p => p.Id == product.Id)
                        .Select(p => new
                        {
                            p.Id,
                            p.Name,
                            p.Price,
                            p.CreatedAtUtc
                        })
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken);

                    var replicaNode = await readDb.Database
                        .SqlQuery<string>($"SELECT inet_server_addr()::text AS \"Value\"")
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken);

                    var primaryNode = await writeDb.Database
                        .SqlQuery<string>($"SELECT inet_server_addr()::text AS \"Value\"")
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken);

                    return Results.Ok(new
                    {
                        WrittenProductId = product.Id,
                        Replica = new
                        {
                            ServedByNode = replicaNode,
                            Seen = productFromReplica is not null,
                            Product = productFromReplica,
                        },
                        Primary = new
                        {
                            ServedByNode = primaryNode,
                            Seen = productFromPrimary is not null,
                            Product = productFromPrimary,
                        }
                    });
                });

            // GET /products — read through HAProxy :5001 → replica nodes (round-robin)
            // Each request may land on a different replica — see inet_server_addr() in the response to verify
            endpoints.MapGet("/products",
                async (ApplicationReadDbContext readDb, CancellationToken cancellationToken) =>
                {
                    var products = await readDb.Products
                        .OrderByDescending(p => p.CreatedAtUtc)
                        .ToListAsync(cancellationToken: cancellationToken);

                    // inet_server_addr() returns the IP of the PostgreSQL node that served this query —
                    // useful to confirm round-robin load balancing across replicas
                    // Notice the AS "Value" alias. When EF Core maps to a primitive type, it expects a property named "Value".
                    // The quotes preserve the exact casing (PostgreSQL lowercases unquoted identifiers by default).
                    var serverAddr = await readDb.Database
                        .SqlQuery<string>($"SELECT inet_server_addr()::text AS \"Value\"")
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken);

                    return new
                    {
                        ServedByNode = serverAddr,
                        ProductsCount = products.Count,
                        Products = products
                    };
                });
        }
    }
}