using Microsoft.EntityFrameworkCore;

namespace PostgresPatroniHaproxyEfcoreDemo.Data;

public class ApplicationReadDbContext(DbContextOptions<ApplicationReadDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationReadDbContext).Assembly,
            type => type.FullName?.Contains("Configurations.Read") ?? false);
    }
}