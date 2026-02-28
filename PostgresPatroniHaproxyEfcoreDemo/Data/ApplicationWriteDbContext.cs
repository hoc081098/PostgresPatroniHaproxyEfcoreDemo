using Microsoft.EntityFrameworkCore;
using PostgresPatroniHaproxyEfcoreDemo.Data.Entities;

namespace PostgresPatroniHaproxyEfcoreDemo.Data;

public class ApplicationWriteDbContext(DbContextOptions<ApplicationWriteDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationWriteDbContext).Assembly,
            type => type.FullName?.Contains("Configurations.Write") ?? false);
    }
}