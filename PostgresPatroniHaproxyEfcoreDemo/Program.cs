using Ardalis.GuardClauses;
using Microsoft.EntityFrameworkCore;
using PostgresPatroniHaproxyEfcoreDemo.Data;
using PostgresPatroniHaproxyEfcoreDemo.Data.Entities;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<ApplicationReadDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("ReadDatabase");
    Guard.Against.NullOrWhiteSpace(connectionString);
    options
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention()
        // Read-only context — disable change tracking globally to reduce memory overhead.
        // Equivalent to appending .AsNoTracking() on every query, but applied at the context level.
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});
builder.Services.AddDbContext<ApplicationWriteDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("WriteDatabase");
    Guard.Against.NullOrWhiteSpace(connectionString);
    options
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Apply pending EF Core migrations on startup (write DB only — replicas receive schema via WAL replication)
    using var scope = app.Services.CreateScope();
    var writeDb = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
    await writeDb.Database.MigrateAsync();
}

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// GET / — health check: verify connectivity to both write and read DB via HAProxy
app.MapGet("/",
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
app.MapPost("/products",
    async (ProductRequest request, ApplicationWriteDbContext writeDb) =>
    {
        var product = Product.Create(request.Name, request.Price, DateTimeOffset.UtcNow);

        writeDb.Products.Add(product);
        await writeDb.SaveChangesAsync();

        return Results.Created($"/products/{product.Id}", product);
    });

// POST /products/read-your-writes-demo — write to primary, then immediately read
// from replica (:5001) and primary (:5000) to demonstrate potential stale reads.
app.MapPost("/products/read-your-writes-demo",
    async (ProductRequest request, ApplicationReadDbContext readDb, ApplicationWriteDbContext writeDb) =>
    {
        var product = Product.Create(request.Name, request.Price, DateTimeOffset.UtcNow);

        writeDb.Products.Add(product);
        await writeDb.SaveChangesAsync();

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
            .FirstOrDefaultAsync();

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
            .FirstOrDefaultAsync();

        var replicaNode = await readDb.Database
            .SqlQuery<string>($"SELECT inet_server_addr()::text AS \"Value\"")
            .FirstOrDefaultAsync();

        var primaryNode = await writeDb.Database
            .SqlQuery<string>($"SELECT inet_server_addr()::text AS \"Value\"")
            .FirstOrDefaultAsync();

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
app.MapGet("/products", async (ApplicationReadDbContext readDb) =>
{
    var products = await readDb.Products
        .OrderByDescending(p => p.CreatedAtUtc)
        .ToListAsync();

    // inet_server_addr() returns the IP of the PostgreSQL node that served this query —
    // useful to confirm round-robin load balancing across replicas
    // Notice the AS "Value" alias. When EF Core maps to a primitive type, it expects a property named "Value".
    // The quotes preserve the exact casing (PostgreSQL lowercases unquoted identifiers by default).
    var serverAddr = await readDb.Database
        .SqlQuery<string>($"SELECT inet_server_addr()::text AS \"Value\"")
        .FirstOrDefaultAsync();

    return new
    {
        ServedByNode = serverAddr,
        ProductsCount = products.Count,
        Products = products
    };
});

app.Run();

sealed record ProductRequest(string Name, decimal Price);