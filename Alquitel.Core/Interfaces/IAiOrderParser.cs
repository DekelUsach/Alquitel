using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    /// <summary>Producto del catálogo tal como se le presenta a la IA (referencia corta, sin GUIDs).</summary>
    public sealed record AiCatalogProduct(int Ref, string Description, string Category);

    /// <summary>Ítem detectado por la IA: referencia al catálogo + cantidad pedida.</summary>
    public sealed record AiParsedItem(int Ref, int Quantity, string? RequestedMeasure);

    /// <summary>
    /// Resultado del análisis: ítems mapeados al catálogo, días detectados (si el texto
    /// los menciona) y pedidos que la IA no pudo asociar a ningún producto.
    /// </summary>
    public sealed record AiOrderParseResult(
        IReadOnlyList<AiParsedItem> Items,
        int? Days,
        IReadOnlyList<string> Unmatched);

    /// <summary>
    /// Analiza el texto libre de un cliente (mail, WhatsApp) contra el catálogo y
    /// devuelve los productos y cantidades solicitados. Implementación de referencia:
    /// Pollinations.ai (Infrastructure). Si no hay API key configurada,
    /// <see cref="IsConfigured"/> es false y el llamador usa el motor local.
    /// </summary>
    public interface IAiOrderParser
    {
        bool IsConfigured { get; }

        Task<AiOrderParseResult?> ParseOrderAsync(
            string customerText,
            IReadOnlyList<AiCatalogProduct> catalog,
            CancellationToken cancellationToken = default);
    }
}
