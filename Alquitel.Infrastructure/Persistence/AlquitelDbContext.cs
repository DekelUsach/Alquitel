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

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // The database file is stored locally, but OneDrive sync may lock it.
            // Ensure journaling mode is set to WAL for better concurrency.
            optionsBuilder.UseSqlite("Data Source=Alquitel.db");
        }

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
