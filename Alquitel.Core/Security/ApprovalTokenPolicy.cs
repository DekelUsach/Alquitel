using System;

namespace Alquitel.Core.Security
{
    /// <summary>
    /// Vigencia del link público de aprobación. Conocer el token ES la autorización, así
    /// que un link sin vencimiento queda válido para siempre en la bandeja de entrada del
    /// cliente (y en cualquier reenvío suyo): un año después, alguien que reciba el mail
    /// puede seguir viendo importes y aprobar el presupuesto.
    ///
    /// El mismo umbral se aplica en dos lugares y debe mantenerse sincronizado:
    ///  - acá, para no reutilizar links vencidos al reenviar el presupuesto;
    ///  - en <c>supabase/functions/aprobar/index.ts</c> (constante APPROVAL_MAX_AGE_DAYS),
    ///    que es quien realmente le cierra la puerta al cliente.
    /// </summary>
    public static class ApprovalTokenPolicy
    {
        public const int MaxAgeDays = 30;

        public static bool IsExpired(DateTime createdAtUtc, DateTime nowUtc, int maxAgeDays = MaxAgeDays)
        {
            if (maxAgeDays < 1) maxAgeDays = MaxAgeDays;
            return nowUtc - createdAtUtc > TimeSpan.FromDays(maxAgeDays);
        }

        /// <summary>Días que le quedan al link (0 si ya venció).</summary>
        public static int RemainingDays(DateTime createdAtUtc, DateTime nowUtc, int maxAgeDays = MaxAgeDays)
        {
            if (maxAgeDays < 1) maxAgeDays = MaxAgeDays;
            var remaining = (createdAtUtc.AddDays(maxAgeDays) - nowUtc).TotalDays;
            return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
        }
    }
}
