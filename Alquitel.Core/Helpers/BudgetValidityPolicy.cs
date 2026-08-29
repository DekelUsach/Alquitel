using System;

namespace Alquitel.Core.Helpers
{
    public enum BudgetValidityState
    {
        /// <summary>Dentro del plazo, sin urgencia.</summary>
        Vigente,
        /// <summary>Vence en los próximos días: conviene llamar al cliente.</summary>
        PorVencer,
        /// <summary>Pasó el plazo: los precios ya no se sostienen.</summary>
        Vencido,
        /// <summary>El estado de la orden hace que el plazo no aplique (aprobada, OT, archivada).</summary>
        NoAplica
    }

    public readonly record struct BudgetValidity(BudgetValidityState State, int DaysRemaining)
    {
        public string Label => State switch
        {
            BudgetValidityState.Vencido => DaysRemaining == -1 ? "Vencido hace 1 día" : $"Vencido hace {-DaysRemaining} días",
            BudgetValidityState.PorVencer => DaysRemaining == 0
                ? "Vence hoy"
                : DaysRemaining == 1 ? "Vence mañana" : $"Vence en {DaysRemaining} días",
            BudgetValidityState.Vigente => $"Vigente ({DaysRemaining} días)",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Vigencia comercial de un presupuesto todavía sin respuesta. Un presupuesto en
    /// borrador de hace dos meses tiene precios que ya no se sostienen; hoy nada en la
    /// app lo señalaba y el vendedor se enteraba al facturar.
    ///
    /// Solo aplica a órdenes en <c>Draft</c>: una vez aprobada, rechazada o archivada,
    /// el plazo dejó de importar.
    /// </summary>
    public static class BudgetValidityPolicy
    {
        /// <summary>Plazo estándar de validez de un presupuesto, en días corridos.</summary>
        public const int DefaultValidityDays = 15;

        /// <summary>Umbral de "por vencer" (días restantes o menos).</summary>
        public const int WarningThresholdDays = 3;

        public static BudgetValidity Evaluate(
            DateTime createdDateUtc,
            bool isPending,
            DateTime nowUtc,
            int validityDays = DefaultValidityDays)
        {
            if (!isPending) return new BudgetValidity(BudgetValidityState.NoAplica, 0);
            if (validityDays < 1) validityDays = DefaultValidityDays;

            // Días corridos completos: un presupuesto emitido hoy vence dentro de
            // validityDays, sin importar la hora exacta de emisión.
            var expiresOn = createdDateUtc.Date.AddDays(validityDays);
            int remaining = (int)(expiresOn - nowUtc.Date).TotalDays;

            var state = remaining < 0
                ? BudgetValidityState.Vencido
                : remaining <= WarningThresholdDays
                    ? BudgetValidityState.PorVencer
                    : BudgetValidityState.Vigente;

            return new BudgetValidity(state, remaining);
        }
    }
}
