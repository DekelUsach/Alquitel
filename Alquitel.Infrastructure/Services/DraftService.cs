using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación en disco de <see cref="IDraftService"/>. Los borradores viven en
    /// %AppData%\Alquitel\Drafts como JSON legible (uno por orden en curso).
    /// </summary>
    public class DraftService : IDraftService
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly string _draftsFolder;

        public DraftService() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alquitel", "Drafts"))
        {
        }

        /// <summary>Carpeta inyectable para tests.</summary>
        public DraftService(string draftsFolder)
        {
            _draftsFolder = draftsFolder;
            Directory.CreateDirectory(_draftsFolder);
        }

        private string PathFor(Guid orderId) =>
            Path.Combine(_draftsFolder, orderId == Guid.Empty ? "new_draft.json" : $"draft_{orderId}.json");

        public async Task SaveDraftAsync(Order order, IReadOnlyList<OrderItem> items, CancellationToken token = default)
        {
            var draft = new OrderDraft
            {
                Id = order.Id,
                BudgetNumber = order.BudgetNumber,
                AdminName = order.AdminName,
                CreatedByUserId = order.CreatedByUserId,
                ClientName = order.Client?.CompanyName,
                ClientCuit = order.Client?.Cuit,
                LocationName = order.Location?.Name,
                EventDate = order.EventDate,
                EventEndDate = order.EventEndDate,
                CreatedDate = order.CreatedDate,
                Comments = order.Comments,
                Status = order.Status.ToString(),
                DiscountPercent = order.DiscountPercent,
                DiscountAmount = order.DiscountAmount,
                AddVat = order.AddVat,
                Items = items.Select(i => new DraftItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    Dias = i.Dias,
                    UnitPrice = i.UnitPrice,
                    TechnicalNotes = i.TechnicalNotes,
                    ImagePath = i.ImagePath,
                    CustomFieldsJson = i.CustomFieldsJson,
                    DescriptionSnapshot = i.DescriptionSnapshot,
                    RequestedMeasure = i.RequestedMeasure,
                }).ToList()
            };

            var json = JsonSerializer.Serialize(draft, WriteOptions);
            await File.WriteAllTextAsync(PathFor(order.Id), json, token);
        }

        public void DeleteDraft(Guid orderId) => DeleteDraftFile(PathFor(orderId));

        public void DeleteDraftFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Could not delete draft {Path}", filePath);
            }
        }

        public IReadOnlyList<DraftInfo> GetRecentDrafts(TimeSpan maxAge)
        {
            try
            {
                var cutoff = DateTime.Now - maxAge;
                return Directory.EnumerateFiles(_draftsFolder, "*.json")
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime >= cutoff)
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new DraftInfo(f.FullName, f.LastWriteTime))
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "GetRecentDrafts failed");
                return Array.Empty<DraftInfo>();
            }
        }

        public async Task<OrderDraft?> LoadDraftAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<OrderDraft>(json, ReadOptions);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadDraftAsync failed for {Path}", filePath);
                return null;
            }
        }
    }
}
