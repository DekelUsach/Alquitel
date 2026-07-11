using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Persistence.Repositories
{
    /// <summary>Implementación EF Core (SQLite local) de <see cref="IOrderRepository"/>.</summary>
    public class EfOrderRepository : IOrderRepository
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;

        public EfOrderRepository(IDbContextFactory<AlquitelDbContext> factory) => _factory = factory;

        public async Task<List<Order>> GetRecentAsync(int count)
        {
            using var db = await _factory.CreateDbContextAsync();
            // IgnoreQueryFilters: el historial debe mostrar órdenes de clientes o
            // productos archivados sin romper las relaciones.
            return await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Include(o => o.Items)
                .OrderByDescending(o => o.CreatedDate)
                .Take(count)
                .ToListAsync();
        }

        public async Task<Order?> GetByIdAsync(Guid id)
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == id);
        }

        public async Task<Order?> GetByBudgetNumberAsync(string budgetNumber)
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .FirstOrDefaultAsync(o => o.BudgetNumber == budgetNumber);
        }

        public async Task<List<string>> GetAllBudgetNumbersAsync()
        {
            using var db = await _factory.CreateDbContextAsync();
            return await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Select(o => o.BudgetNumber)
                .ToListAsync();
        }

        public async Task<int> GetCommittedQuantityAsync(Guid productId, DateTime from, DateTime to, Guid excludeOrderId)
        {
            using var db = await _factory.CreateDbContextAsync();

            // El solapamiento de rangos se resuelve en memoria: EventDate.AddDays(Dias)
            // no traduce de forma confiable en todos los providers, y el volumen de
            // órdenes activas es chico (decenas, no miles).
            var candidates = await db.OrderItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.ProductId == productId)
                .Join(
                    db.Orders.IgnoreQueryFilters().Where(o =>
                        o.Id != excludeOrderId &&
                        o.EventDate != null &&
                        (o.Status == OrderStatus.Approved ||
                         o.Status == OrderStatus.SentToOF ||
                         o.Status == OrderStatus.SentToOT)),
                    i => i.OrderId,
                    o => o.Id,
                    (i, o) => new { o.EventDate, i.Dias, i.Quantity })
                .ToListAsync();

            return candidates
                .Where(c =>
                {
                    var start = c.EventDate!.Value.Date;
                    var end = start.AddDays(Math.Max(1, c.Dias));
                    return start < to && end > from;
                })
                .Sum(c => c.Quantity);
        }

        public async Task<UserOrderStats> GetUserStatsAsync(Guid userId, string userName)
        {
            using var db = await _factory.CreateDbContextAsync();

            var orders = db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.CreatedByUserId == userId || o.AdminName == userName);

            var count = await orders.CountAsync();
            if (count == 0)
                return new UserOrderStats(0, 0m, null, null);

            var last = await orders
                .OrderByDescending(o => o.CreatedDate)
                .Select(o => new { o.CreatedDate, o.BudgetNumber })
                .FirstAsync();

            var total = await db.OrderItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => db.Orders.IgnoreQueryFilters().Any(o =>
                    o.Id == i.OrderId && (o.CreatedByUserId == userId || o.AdminName == userName)))
                .SumAsync(i => (decimal?)(i.Quantity * i.UnitPrice * i.Dias)) ?? 0m;

            return new UserOrderStats(count, total, last.CreatedDate, last.BudgetNumber);
        }

        public async Task UpsertAsync(Order order)
        {
            using var db = await _factory.CreateDbContextAsync();
            var existing = await db.Orders.IgnoreQueryFilters()
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            if (existing == null)
            {
                db.Orders.Add(order);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(order);
                // Sincronización simple de items: reemplaza la colección completa.
                db.RemoveRange(existing.Items);
                existing.Items = order.Items;
            }
            await db.SaveChangesAsync();
        }
    }
}
