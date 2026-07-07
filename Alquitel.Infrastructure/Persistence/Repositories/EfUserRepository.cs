using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Persistence.Repositories
{
    /// <summary>Implementación EF Core de <see cref="IUserRepository"/> (SQLite o PostgreSQL según provider).</summary>
    public class EfUserRepository : IUserRepository
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;

        public EfUserRepository(IDbContextFactory<AlquitelDbContext> factory) => _factory = factory;

        public async Task<List<User>> GetActiveAsync()
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Users.AsNoTracking()
                .Where(u => !u.IsArchived)
                .OrderBy(u => u.Name)
                .ToListAsync();
        }

        public async Task<User?> GetByIdAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        }

        public async Task UpsertAsync(User user)
        {
            using var db = await _factory.CreateDbContextAsync();
            var existing = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id);
            if (existing == null)
                db.Users.Add(user);
            else
                db.Entry(existing).CurrentValues.SetValues(user);
            await db.SaveChangesAsync();
        }

        public async Task ArchiveAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return;
            user.IsArchived = true;
            await db.SaveChangesAsync();
        }
    }
}
