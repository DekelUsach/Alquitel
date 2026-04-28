using Alquitel.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Persistence
{
    public class AlquitelDbContext : DbContext
    {
        public DbSet<Client> Clients { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Location> Locations { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }

        public AlquitelDbContext(DbContextOptions<AlquitelDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── Unique constraints ───────────────────────────────
            modelBuilder.Entity<Client>().HasIndex(c => c.Cuit).IsUnique();
            modelBuilder.Entity<Order>().HasIndex(o => o.BudgetNumber).IsUnique();

            // ── Performance indices ──────────────────────────────
            modelBuilder.Entity<Order>().HasIndex(o => o.CreatedDate);
            modelBuilder.Entity<Order>().HasIndex(o => o.ClientId);
            modelBuilder.Entity<OrderItem>().HasIndex(oi => oi.OrderId);

            // ── Relationships ────────────────────────────────────
            modelBuilder.Entity<Order>()
                .HasOne(o => o.Client)
                .WithMany()
                .HasForeignKey(o => o.ClientId);

            modelBuilder.Entity<OrderItem>()
                .HasOne(oi => oi.Product)
                .WithMany()
                .HasForeignKey(oi => oi.ProductId);

            // ── Soft-delete global query filters ─────────────────
            // Archived entities are excluded by default from all queries.
            // Use .IgnoreQueryFilters() when you need to include them.
            modelBuilder.Entity<Product>().HasQueryFilter(p => !p.IsArchived);
            modelBuilder.Entity<Client>().HasQueryFilter(c => !c.IsArchived);
        }
    }
}
