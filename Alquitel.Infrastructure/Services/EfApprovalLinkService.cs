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

                // Antes se reutilizaba el link pendiente al reenviar el mail, para no
                // invalidar el que el cliente quizá ya tenía abierto. Eso YA NO ES
                // POSIBLE, y es a propósito: desde la migración
                // supabase/migrations/20260829000700_approval_tokens_hashed.sql la base
                // guarda únicamente el SHA-256 del token. Un token que no se puede leer
                // de la base tampoco se puede volver a poner en una URL.
                //
                // El costo es que reenviar emite un link nuevo. La contrapartida es la
                // que importa: un volcado de la base, un backup o alguien con el
                // connection string dejan de alcanzar para aprobar presupuestos ajenos.
                //
                // Del lado servidor, un trigger AFTER INSERT revoca los links pendientes
                // anteriores de la misma orden (RevokedAt), así que el link viejo que el
                // cliente pudo haber reenviado a un tercero deja de servir en cuanto se
                // emite el nuevo — con los precios que ese link mostraba, que además
                // pueden haber cambiado.
                var approval = new OrderApproval { OrderId = orderId };
                var token = approval.Token;

                db.OrderApprovals.Add(approval);
                await db.SaveChangesAsync();

                // No loguear el token: es la autorización del link público y los logs
                // de Serilog quedan 30 días en disco legibles por cualquier usuario.
                AppLog.Information("Link de aprobación emitido para orden {OrderId} (approvalId {ApprovalId}); los pendientes anteriores quedan revocados",
                    orderId, approval.Id);

                // `token` es el valor generado en memoria. En PostgreSQL la columna
                // queda en NULL (el trigger hashea y descarta el plano), así que este
                // es el único momento en toda la vida del link en que el texto claro
                // está disponible.
                return BuildUrl(token);
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
                // Proyección explícita SIN "Token": en PostgreSQL esa columna quedó en
                // NULL (ver 20260829000700) y materializarla en un Guid no anulable
                // reventaría acá. Además no hace falta: quien llama solo mira estado y
                // fechas para mostrar "esperando respuesta / aprobado / rechazado".
                return await db.OrderApprovals.AsNoTracking()
                    .Where(a => a.OrderId == orderId)
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(a => new OrderApproval
                    {
                        Id          = a.Id,
                        OrderId     = a.OrderId,
                        // Explícito para que nadie lo confunda con un token real: el
                        // valor no existe fuera del momento de la emisión.
                        Token       = Guid.Empty,
                        Status      = a.Status,
                        CreatedAt   = a.CreatedAt,
                        RespondedAt = a.RespondedAt,
                        ClientIp    = a.ClientIp,
                    })
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
