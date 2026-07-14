using System.Text.RegularExpressions;

namespace Alquitel.Core.Search
{
    /// <summary>
    /// Parser del buscador rápido del catálogo: permite fijar la cantidad como prefijo
    /// ("3*proyector", "2x notebook") para cargar el carrito sin tocar el mouse.
    /// Un número solo ("85", "4k") se trata como término literal, nunca como cantidad:
    /// muchos nombres de producto contienen números.
    /// </summary>
    public static class SearchQueryParser
    {
        private const int MaxQuantity = 999;

        // "3*algo", "3 * algo", "2x algo" — el separador (* o x+espacio) es obligatorio.
        private static readonly Regex QuantityPrefix = new(
            @"^\s*(\d{1,4})\s*(?:\*\s*|[xX]\s+)(.+)$", RegexOptions.Compiled);

        /// <summary>Devuelve (cantidad, término de búsqueda). Cantidad siempre en [1, 999].</summary>
        public static (int Quantity, string Term) Parse(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return (1, string.Empty);

            var match = QuantityPrefix.Match(input);
            if (!match.Success) return (1, input.Trim());

            int qty = int.TryParse(match.Groups[1].Value, out var q) ? q : 1;
            qty = Math.Clamp(qty, 1, MaxQuantity);
            return (qty, match.Groups[2].Value.Trim());
        }
    }
}
