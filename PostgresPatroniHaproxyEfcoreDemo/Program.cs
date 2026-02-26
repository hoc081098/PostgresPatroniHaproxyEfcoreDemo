using Ardalis.GuardClauses;
using Microsoft.EntityFrameworkCore;
using PostgresPatroniHaproxyEfcoreDemo.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<ApplicationReadDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("ReadDatabase");
    Guard.Against.NullOrWhiteSpace(connectionString);
    options.UseNpgsql(connectionString);
});
builder.Services.AddDbContext<ApplicationWriteDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("WriteDatabase");
    Guard.Against.NullOrWhiteSpace(connectionString);
    options.UseNpgsql(connectionString);
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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

app.Run();