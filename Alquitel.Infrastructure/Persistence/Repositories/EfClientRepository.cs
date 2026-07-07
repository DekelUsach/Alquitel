using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// Implementación EF Core (SQLite local) de <see cref="IClientRepository"/>.
    /// Cada operación crea su propio DbContext vía factory (regla de thread-safety
    /// del proyecto). Cuando se migre a Supabase, se agrega una implementación
    /// paralela contra PostgREST/Npgsql y se cambia el registro en DI.
    /// </summary>
    public class EfClientRepository : IClientRepository
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;

        public EfClientRepository(IDbContextFactory<AlquitelDbContext> factory) => _factory = factory;

        public async Task<List<Client>> GetAllAsync()
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Clients.AsNoTracking().OrderBy(c => c.CompanyName).ToListAsync();
        }

        public async Task<Client?> GetByIdAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<Client?> GetByCuitAsync(string cuit)
        {
            using var db = await _factory.CreateDbContextAsync();
            var normalized = cuit.Trim();
            return await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Cuit == normalized);
        }

        public async Task UpsertAsync(Client client)
        {
            using var db = await _factory.CreateDbContextAsync();
            var existing = await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == client.Id);
            if (existing == null)
            {
                db.Clients.Add(client);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(client);
            }
            await db.SaveChangesAsync();
        }

        public async Task ArchiveAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            var client = await db.Clients.FindAsync(id);
            if (client == null) return;
            client.IsArchived = true;
            await db.SaveChangesAsync();
        }
    }
}
