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
            modelBuilder.Entity<Client>().HasIndex(c => c.Cuit).IsUnique();
            modelBuilder.Entity<Order>().HasIndex(o => o.BudgetNumber).IsUnique();
            
            // Configure relationships
            modelBuilder.Entity<Order>()
                .HasOne(o => o.Client)
                .WithMany()
                .HasForeignKey(o => o.ClientId);

            modelBuilder.Entity<OrderItem>()
                .HasOne(oi => oi.Product)
                .WithMany()
                .HasForeignKey(oi => oi.ProductId);
        }
    }
}
