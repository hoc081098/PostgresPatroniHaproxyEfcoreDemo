using Ardalis.GuardClauses;
using Microsoft.EntityFrameworkCore;
using PostgresPatroniHaproxyEfcoreDemo;
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

app.MapDemoEndpoints();

app.Run();