using Microsoft.EntityFrameworkCore;

namespace PostgresPatroniHaproxyEfcoreDemo.Data;

public class ApplicationWriteDbContext(DbContextOptions<ApplicationWriteDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationWriteDbContext).Assembly,
            type => type.FullName?.Contains("Configurations.Write") ?? false);
    }
}