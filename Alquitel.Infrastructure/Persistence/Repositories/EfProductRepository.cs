using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Persistence.Repositories
{
    /// <summary>Implementación EF Core (SQLite local) de <see cref="IProductRepository"/>.</summary>
    public class EfProductRepository : IProductRepository
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;

        public EfProductRepository(IDbContextFactory<AlquitelDbContext> factory) => _factory = factory;

        public async Task<List<Product>> GetAllAsync()
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Products.AsNoTracking()
                .OrderBy(p => p.Category).ThenBy(p => p.Description)
                .ToListAsync();
        }

        public async Task<Product?> GetByIdAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task UpsertAsync(Product product)
        {
            using var db = await _factory.CreateDbContextAsync();
            var existing = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == product.Id);
            if (existing == null)
            {
                db.Products.Add(product);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(product);
            }
            await db.SaveChangesAsync();
        }

        public async Task ArchiveAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            var product = await db.Products.FindAsync(id);
            if (product == null) return;
            product.IsArchived = true;
            await db.SaveChangesAsync();
        }
    }
}
