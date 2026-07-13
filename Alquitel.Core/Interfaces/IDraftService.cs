using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>Ítem serializado de un borrador de pedido. Espeja los campos editables de <see cref="OrderItem"/>.</summary>
    public sealed class DraftItem
    {
        public Guid ProductId { get; set; }
        public int Quantity { get; set; }
        public int Dias { get; set; }
        public decimal UnitPrice { get; set; }
        public string? TechnicalNotes { get; set; }
        public string? ImagePath { get; set; }
        public string? CustomFieldsJson { get; set; }
        public string? DescriptionSnapshot { get; set; }
        public string? RequestedMeasure { get; set; }
    }

    /// <summary>
    /// Borrador de pedido serializado a JSON por el autosave del armador. El esquema debe
    /// espejar cada campo editable de Order/OrderItem o el borrador pierde datos.
    /// </summary>
    public sealed class OrderDraft
    {
        public Guid Id { get; set; }
        public string? BudgetNumber { get; set; }
        public string? AdminName { get; set; }
        public Guid? CreatedByUserId { get; set; }
        public string? ClientName { get; set; }
        public string? ClientCuit { get; set; }
        public string? LocationName { get; set; }
        public DateTime? EventDate { get; set; }
        public DateTime? EventEndDate { get; set; }
        public DateTime CreatedDate { get; set; }
        public string? Comments { get; set; }
        public string? Status { get; set; }
        public decimal DiscountPercent { get; set; }
        public decimal DiscountAmount { get; set; }
        public bool AddVat { get; set; }
        public List<DraftItem> Items { get; set; } = new();
    }

    /// <summary>Borrador encontrado en disco, con su antigüedad para el diálogo de recuperación.</summary>
    public sealed record DraftInfo(string FilePath, DateTime LastWriteLocal);

    /// <summary>
    /// Guardado y recuperación de borradores del armador (%AppData%\Alquitel\Drafts).
    /// El autosave escribe cada 30 s; al abrir el armador se ofrecen los borradores
    /// recientes para no perder un pedido a medias si la app se cerró.
    /// </summary>
    public interface IDraftService
    {
        /// <summary>Serializa y escribe el borrador de la orden actual.</summary>
        Task SaveDraftAsync(Order order, IReadOnlyList<OrderItem> items, CancellationToken token = default);

        /// <summary>Elimina el borrador de la orden (tras persistirla en la base).</summary>
        void DeleteDraft(Guid orderId);

        /// <summary>Elimina un borrador por ruta (tras recuperarlo o descartarlo).</summary>
        void DeleteDraftFile(string filePath);

        /// <summary>Borradores con antigüedad menor a <paramref name="maxAge"/>, el más nuevo primero.</summary>
        IReadOnlyList<DraftInfo> GetRecentDrafts(TimeSpan maxAge);

        /// <summary>Lee y deserializa un borrador; null si el archivo está corrupto o no existe.</summary>
        Task<OrderDraft?> LoadDraftAsync(string filePath);
    }
}
