using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>Implementación EF de <see cref="IOrderAuditService"/>: firma con el usuario logueado.</summary>
    public class EfOrderAuditService : IOrderAuditService
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;
        private readonly ICurrentUserService _currentUser;

        public EfOrderAuditService(IDbContextFactory<AlquitelDbContext> factory, ICurrentUserService currentUser)
        {
            _factory = factory;
            _currentUser = currentUser;
        }

        public async Task LogAsync(Guid orderId, string eventType, string? detail = null)
        {
            try
            {
                using var db = await _factory.CreateDbContextAsync();
                db.OrderAuditEvents.Add(new OrderAuditEvent
                {
                    OrderId = orderId,
                    UserName = _currentUser.Current?.Name ?? "(desconocido)",
                    UserId = _currentUser.Current?.Id,
                    EventType = eventType,
                    Detail = detail,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // La bitácora nunca rompe la operación principal.
                AppLog.Warning(ex, "Audit log failed for order {OrderId} ({EventType})", orderId, eventType);
            }
        }

        public async Task<List<OrderAuditEvent>> GetForOrderAsync(Guid orderId)
        {
            try
            {
                using var db = await _factory.CreateDbContextAsync();
                return await db.OrderAuditEvents.AsNoTracking()
                    .Where(e => e.OrderId == orderId)
                    .OrderByDescending(e => e.Timestamp)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "GetForOrderAsync failed for order {OrderId}", orderId);
                return new List<OrderAuditEvent>();
            }
        }
    }
}
