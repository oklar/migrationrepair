using Microsoft.EntityFrameworkCore;
using MigrationTester.Examples.Entities;

namespace MigrationTester.Examples;

public sealed class ScenarioDbContext : DbContext
{
    public ScenarioDbContext(DbContextOptions<ScenarioDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable(
                "Products",
                t => t.HasCheckConstraint("CK_Products_PositivePrice", "[Price] > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).IsRequired().HasMaxLength(50);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Note);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(256).IsUnicode(false);
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32);
            e.HasOne(x => x.Customer)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.ToTable("Tags");
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).IsRequired().HasMaxLength(128);
            e.Property(x => x.ExternalId).HasColumnType("uniqueidentifier");
            e.HasOne(x => x.Product)
                .WithMany(x => x.Tags)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Warehouse>(e =>
        {
            e.ToTable("Warehouses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<OrderLine>(e =>
        {
            e.ToTable("OrderLines");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Order)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product)
                .WithMany(x => x.OrderLines)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
