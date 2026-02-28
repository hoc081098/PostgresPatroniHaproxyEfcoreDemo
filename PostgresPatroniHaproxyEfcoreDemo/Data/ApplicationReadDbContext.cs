using Microsoft.EntityFrameworkCore;
using PostgresPatroniHaproxyEfcoreDemo.Data.Entities;

namespace PostgresPatroniHaproxyEfcoreDemo.Data;

// UseQueryTrackingBehavior(NoTracking): read-only context — no change tracking needed,
// which reduces memory overhead and improves query performance.
public class ApplicationReadDbContext(DbContextOptions<ApplicationReadDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationReadDbContext).Assembly,
            type => type.FullName?.Contains("Configurations.Read") ?? false);
    }
}