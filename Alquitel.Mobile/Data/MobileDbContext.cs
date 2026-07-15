using Alquitel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Location = Alquitel.Core.Entities.Location;

namespace Alquitel.Mobile.Data;

/// <summary>
/// Contexto EF de la app mobile contra la base compartida de Supabase (PostgreSQL).
/// Replica el mapeo relevante de AlquitelDbContext (desktop). No corre migraciones:
/// el schema lo gobiernan las migraciones existentes del desktop/Supabase.
/// </summary>
public class MobileDbContext : DbContext
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OrderAuditEvent> OrderAuditEvents => Set<OrderAuditEvent>();
    public DbSet<OrderApproval> OrderApprovals => Set<OrderApproval>();
    public DbSet<EventTemplate> EventTemplates => Set<EventTemplate>();
    public DbSet<UserMobilePermission> UserMobilePermissions => Set<UserMobilePermission>();

    public MobileDbContext(DbContextOptions<MobileDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>().HasIndex(c => c.Cuit).IsUnique().HasFilter("\"Cuit\" <> ''");
        modelBuilder.Entity<Order>().HasIndex(o => o.BudgetNumber).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.Name).IsUnique();
        modelBuilder.Entity<EventTemplate>().HasIndex(t => t.Name).IsUnique();
        modelBuilder.Entity<OrderApproval>().HasIndex(a => a.Token).IsUnique();
        modelBuilder.Entity<OrderApproval>().HasIndex(a => a.OrderId);

        modelBuilder.Entity<Order>().HasIndex(o => o.CreatedDate);
        modelBuilder.Entity<Order>().HasIndex(o => o.ClientId);
        modelBuilder.Entity<OrderItem>().HasIndex(oi => oi.OrderId);
        modelBuilder.Entity<OrderAuditEvent>().HasIndex(e => e.OrderId);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.Client)
            .WithMany()
            .HasForeignKey(o => o.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.Location)
            .WithMany()
            .HasForeignKey(o => o.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.Product)
            .WithMany()
            .HasForeignKey(oi => oi.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Product>().HasQueryFilter(p => !p.IsArchived);
        modelBuilder.Entity<Client>().HasQueryFilter(c => !c.IsArchived);
    }
}
