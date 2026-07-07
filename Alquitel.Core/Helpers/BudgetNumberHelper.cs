using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Numeración de presupuestos. Formato: número de serie entero ("31294") con
    /// versiones opcionales como ramas del mismo pedido ("31294/2", "31294/3").
    /// El nombre de archivo usa la forma "31294(2)" porque '/' no es válido en Windows.
    /// </summary>
    public static class BudgetNumberHelper
    {
        /// <summary>Parte de serie del número: "31294/2" → "31294". Trim incluido.</summary>
        public static string BasePart(string? budgetNumber)
        {
            var n = (budgetNumber ?? string.Empty).Trim();
            int slash = n.IndexOf('/');
            return slash < 0 ? n : n[..slash];
        }

        /// <summary>Versión del número: "31294/2" → 2; sin sufijo → 1.</summary>
        public static int VersionPart(string? budgetNumber)
        {
            var n = (budgetNumber ?? string.Empty).Trim();
            int slash = n.IndexOf('/');
            if (slash < 0) return 1;
            return int.TryParse(n[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 1 ? v : 1;
        }

        /// <summary>
        /// Próximo número de serie: máximo de las bases numéricas existentes + 1.
        /// Con base vacía arranca en 1. Los números no numéricos se ignoran.
        /// </summary>
        public static string NextSerial(IEnumerable<string?> existingNumbers)
        {
            long max = 0;
            foreach (var n in existingNumbers)
            {
                if (long.TryParse(BasePart(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
                    max = value;
            }
            return (max + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Próximo número de versión para la rama del número dado:
        /// NextVersion("31294", ["31294", "31294/2"]) → "31294/3".
        /// </summary>
        public static string NextVersion(string budgetNumber, IEnumerable<string?> existingNumbers)
        {
            string basePart = BasePart(budgetNumber);
            if (string.IsNullOrEmpty(basePart))
                throw new ArgumentException("El presupuesto no tiene número base.", nameof(budgetNumber));

            int maxVersion = existingNumbers
                .Where(n => string.Equals(BasePart(n), basePart, StringComparison.Ordinal))
                .Select(VersionPart)
                .DefaultIfEmpty(1)
                .Max();

            return $"{basePart}/{maxVersion + 1}";
        }

        /// <summary>Forma segura para nombre de archivo: "31294/2" → "31294(2)".</summary>
        public static string ToFileNameForm(string? budgetNumber)
        {
            var n = (budgetNumber ?? string.Empty).Trim();
            int slash = n.IndexOf('/');
            return slash < 0 ? n : $"{n[..slash]}({n[(slash + 1)..]})";
        }

        /// <summary>Inversa de <see cref="ToFileNameForm"/>: "31294(2)" → "31294/2".</summary>
        public static string FromFileNameForm(string? fileNumber)
        {
            var n = (fileNumber ?? string.Empty).Trim();
            int open = n.IndexOf('(');
            if (open <= 0 || !n.EndsWith(')')) return n;
            return $"{n[..open]}/{n[(open + 1)..^1]}";
        }
    }
}
