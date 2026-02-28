using Ardalis.GuardClauses;

namespace PostgresPatroniHaproxyEfcoreDemo.Data.Entities;

public class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public decimal Price { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// EF Core requires a parameterless constructor for materialization.
    /// This constructor is private to prevent direct instantiation of the Product class without using the Create factory method.
    /// </summary>
    private Product()
    {
    }

    public static Product Create(string name, decimal price, DateTimeOffset createdAtUtc)
    {
        Guard.Against.NullOrEmpty(name);
        Guard.Against.NegativeOrZero(price);

        var id = Guid.CreateVersion7();

        return new Product
        {
            Id = id,
            Name = name,
            Price = price,
            CreatedAtUtc = createdAtUtc
        };
    }
}