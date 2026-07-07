using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Persistence.Repositories
{
    /// <summary>Implementación EF Core (SQLite local) de <see cref="ILocationRepository"/>.</summary>
    public class EfLocationRepository : ILocationRepository
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;

        public EfLocationRepository(IDbContextFactory<AlquitelDbContext> factory) => _factory = factory;

        public async Task<List<Location>> GetAllAsync()
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync();
        }

        public async Task<Location?> GetByIdAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        }

        public async Task UpsertAsync(Location location)
        {
            using var db = await _factory.CreateDbContextAsync();
            var existing = await db.Locations.FirstOrDefaultAsync(l => l.Id == location.Id);
            if (existing == null)
            {
                db.Locations.Add(location);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(location);
            }
            await db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            var location = await db.Locations.FindAsync(id);
            if (location == null) return;
            db.Locations.Remove(location);
            await db.SaveChangesAsync();
        }
    }
}
