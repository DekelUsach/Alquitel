using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.Services;

/// <summary>
/// Links públicos de aprobación (Edge Function "aprobar" de Supabase).
/// Mismo formato de URL que EfApprovalLinkService del desktop.
/// </summary>
public class ApprovalService
{
    private readonly IDbContextFactory<MobileDbContext> _factory;

    public ApprovalService(IDbContextFactory<MobileDbContext> factory) => _factory = factory;

    public string BuildUrl(Guid token) => $"{AppConfig.SupabaseUrl}/functions/v1/aprobar?token={token:D}";

    /// <summary>Reutiliza el link pendiente si existe; si no, crea uno nuevo.</summary>
    public async Task<string> GetOrCreateLinkAsync(Guid orderId)
    {
        using var db = _factory.CreateDbContext();

        var existing = await db.OrderApprovals
            .Where(a => a.OrderId == orderId && a.Status == ApprovalStatus.Pending)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
        if (existing != null) return BuildUrl(existing.Token);

        var approval = new OrderApproval { OrderId = orderId };
        db.OrderApprovals.Add(approval);
        await db.SaveChangesAsync();
        return BuildUrl(approval.Token);
    }

    public async Task<List<OrderApproval>> GetForOrderAsync(Guid orderId)
    {
        using var db = _factory.CreateDbContext();
        return await db.OrderApprovals
            .Where(a => a.OrderId == orderId)
            .OrderByDescending(a => a.CreatedAt)
            .AsNoTracking()
            .ToListAsync();
    }
}
