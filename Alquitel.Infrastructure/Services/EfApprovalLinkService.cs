using System;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación EF de <see cref="IApprovalLinkService"/>: inserta la fila de
    /// OrderApprovals en la base (compartida) y arma la URL de la Edge Function
    /// "aprobar" del proyecto Supabase. Ver docs/APPROVAL_PORTAL.md para el deploy
    /// de la función y la migración SQL del lado servidor.
    /// </summary>
    public class EfApprovalLinkService : IApprovalLinkService
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;
        private readonly string _supabaseUrl;

        public EfApprovalLinkService(IDbContextFactory<AlquitelDbContext> factory, string? supabaseUrl)
        {
            _factory = factory;
            _supabaseUrl = (supabaseUrl ?? string.Empty).TrimEnd('/');
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_supabaseUrl);

        public async Task<string?> CreateApprovalLinkAsync(Guid orderId)
        {
            if (!IsConfigured) return null;
            try
            {
                using var db = await _factory.CreateDbContextAsync();

                if (!await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.Id == orderId))
                {
                    AppLog.Warning("CreateApprovalLinkAsync: la orden {OrderId} no está persistida", orderId);
                    return null;
                }

                // Reutilizar el link pendiente si ya existe (reenviar el mismo mail no
                // debe invalidar el link anterior que el cliente quizá ya tiene abierto).
                var approval = await db.OrderApprovals
                    .Where(a => a.OrderId == orderId && a.Status == ApprovalStatus.Pending)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync();

                if (approval == null)
                {
                    approval = new OrderApproval { OrderId = orderId };
                    db.OrderApprovals.Add(approval);
                    await db.SaveChangesAsync();
                    // No loguear el token: es la autorización del link público y los logs
                    // de Serilog quedan 30 días en disco legibles por cualquier usuario.
                    AppLog.Information("Link de aprobación creado para orden {OrderId} (approvalId {ApprovalId})",
                        orderId, approval.Id);
                }

                return BuildUrl(approval.Token);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "CreateApprovalLinkAsync failed for order {OrderId}", orderId);
                return null;
            }
        }

        public async Task<OrderApproval?> GetLatestForOrderAsync(Guid orderId)
        {
            try
            {
                using var db = await _factory.CreateDbContextAsync();
                return await db.OrderApprovals.AsNoTracking()
                    .Where(a => a.OrderId == orderId)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "GetLatestForOrderAsync failed for order {OrderId}", orderId);
                return null;
            }
        }

        // Formato "D" (con guiones): la Edge Function lo compara directo contra la
        // columna uuid sin re-formatear.
        private string BuildUrl(Guid token) => $"{_supabaseUrl}/functions/v1/aprobar?token={token:D}";
    }
}
