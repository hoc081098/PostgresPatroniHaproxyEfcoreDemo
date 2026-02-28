using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PostgresPatroniHaproxyEfcoreDemo.Data.Entities;

namespace PostgresPatroniHaproxyEfcoreDemo.Data.Configurations.Read;

public class ProductReadConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        // Read configuration intentionally omits DDL-related constraints (IsRequired, HasMaxLength,
        // HasColumnType, UseIdentityByDefaultColumn) — those only affect migration-generated DDL,
        // which is the responsibility of the Write context.
        // Read context only needs enough mapping for EF to build correct SELECT queries.
        builder.HasKey(p => p.Id);
    }
}
