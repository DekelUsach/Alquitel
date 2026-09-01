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
    /// Implementación en disco de <see cref="IOrderOutboxService"/> (patrón espejo de
    /// DraftService): un JSON por orden fallida en %AppData%\Alquitel\Outbox. Un timer
    /// reintenta cada 5 minutos; al lograr persistir, el archivo se elimina.
    /// </summary>
    public class OrderOutboxService : IOrderOutboxService, IDisposable
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly string _outboxFolder;
        private readonly IOrderPersistenceService _persistence;
        private readonly LocalProtectedFileStore _store;
        private readonly SemaphoreSlim _retryLock = new(1, 1);
        private Timer? _timer;

        public OrderOutboxService(IOrderPersistenceService persistence)
            : this(persistence, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alquitel", "Outbox"))
        {
        }

        /// <summary>Carpeta inyectable para tests.</summary>
        public OrderOutboxService(IOrderPersistenceService persistence, string outboxFolder)
        {
            _persistence = persistence;
            _outboxFolder = outboxFolder;
            Directory.CreateDirectory(_outboxFolder);
            _store = new LocalProtectedFileStore(_outboxFolder);
        }

        /// <summary>Arranca el reintento periódico (primer intento al minuto, luego cada 5).</summary>
        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(async _ =>
            {
                try { await RetryPendingAsync(); }
                catch (Exception ex) { AppLog.Warning("Outbox retry tick failed ({ErrorType})", ex.GetType().Name); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }

        public int PendingCount
        {
            get
            {
                try { return Directory.EnumerateFiles(_outboxFolder, "order_*.json").Count(); }
                catch { return 0; }
            }
        }

        public bool Enqueue(Order order, Guid? operationId = null)
        {
            try
            {
                // Clon plano: las nav props no viajan (Product dentro de los items pesa
                // y no hace falta; PersistAsync resuelve cliente/ubicación por nombre/CUIT).
                var snapshot = new Order
                {
                    Id = order.Id,
                    BudgetNumber = order.BudgetNumber,
                    AdminName = order.AdminName,
                    CreatedByUserId = order.CreatedByUserId,
                    CreatedDate = order.CreatedDate,
                    EventDate = order.EventDate,
                    EventEndDate = order.EventEndDate,
                    Status = order.Status,
                    Comments = order.Comments,
                    DiscountPercent = order.DiscountPercent,
                    DiscountAmount = order.DiscountAmount,
                    AddVat = order.AddVat,
                    RowVersion = order.RowVersion,
                    Client = order.Client == null ? null : new Client
                    {
                        Id = order.Client.Id,
                        CompanyName = order.Client.CompanyName,
                        Cuit = order.Client.Cuit,
                        ContactName = order.Client.ContactName,
                        Phone = order.Client.Phone,
                        Email = order.Client.Email,
                    },
                    Location = order.Location == null ? null : new Location
                    {
                        Id = order.Location.Id,
                        Name = order.Location.Name,
                    },
                    Items = order.Items.Select(i => new OrderItem
                    {
                        Id = i.Id,
                        OrderId = i.OrderId,
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        Dias = i.Dias,
                        TechnicalNotes = i.TechnicalNotes,
                        ImagePath = i.ImagePath,
                        CustomFieldsJson = i.CustomFieldsJson,
                        DescriptionSnapshot = i.DescriptionSnapshot,
                        RequestedMeasure = i.RequestedMeasure,
                    }).ToList(),
                };

                var envelope = new OutboxEnvelope
                {
                    OperationId = operationId ?? Guid.NewGuid(),
                    Order = snapshot,
                };
                _store.WriteJson(PathFor(order.Id), envelope, WriteOptions);
                AppLog.Information("Orden {OrderId} encolada para reintento", order.Id);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error(
                    "No se pudo encolar la orden {OrderId} en el outbox ({ErrorType})",
                    order.Id, ex.GetType().Name);
                return false;
            }
        }

        public async Task<int> RetryPendingAsync()
        {
            if (!await _retryLock.WaitAsync(0)) return 0; // ya hay un reintento en curso
            try
            {
                int saved = 0;
                List<string> files;
                try { files = Directory.EnumerateFiles(_outboxFolder, "order_*.json").ToList(); }
                catch { return 0; }

                foreach (var file in files)
                {
                    var stored = await ReadEnvelopeAsync(file);
                    if (stored == null) continue;
                    var envelope = stored.Value;
                    var order = envelope.Order!;

                    var result = await _persistence.PersistAsync(
                        order, operationId: envelope.OperationId);
                    switch (result.Status)
                    {
                        case OrderPersistStatus.Saved:
                            _store.DeleteIfUnchanged(file, stored.Fingerprint);
                            saved++;
                            AppLog.Information("Outbox: orden {OrderId} persistida en reintento", order.Id);
                            break;
                        case OrderPersistStatus.Conflict:
                            _store.QuarantineIfUnchanged(
                                file, stored.Fingerprint, "concurrency_conflict");
                            AppLog.Warning(
                                "Outbox: orden {OrderId} preservada en cuarentena por conflicto",
                                order.Id);
                            break;
                        case OrderPersistStatus.Error:
                            // Sigue sin conexión (u otro fallo): queda para el próximo tick.
                            break;
                    }
                }
                return saved;
            }
            finally
            {
                _retryLock.Release();
            }
        }

        private string PathFor(Guid orderId) => Path.Combine(_outboxFolder, $"order_{orderId}.json");

        private async Task<LocalProtectedFileStore.StoredJson<OutboxEnvelope>?> ReadEnvelopeAsync(
            string file) => await _store.ReadJsonWithLegacyMigrationAsync<OutboxEnvelope, Order>(
            file,
            ReadOptions,
            current => current.OperationId != Guid.Empty &&
                       current.Order != null &&
                       IsValidOrderPayload(current.Order),
            IsValidOrderPayload,
            legacy => new OutboxEnvelope { OperationId = Guid.NewGuid(), Order = legacy });

        private static bool IsValidOrderPayload(Order order) =>
            order.Id != Guid.Empty &&
            !string.IsNullOrWhiteSpace(order.BudgetNumber) &&
            order.Client != null &&
            order.Location != null &&
            order.Items != null;

        private void TryDelete(string file)
        {
            try { _store.Delete(file); }
            catch (Exception ex) { AppLog.Warning("Outbox: no se pudo borrar una entrada ({ErrorType})", ex.GetType().Name); }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private sealed class OutboxEnvelope
        {
            public Guid OperationId { get; set; }
            public Order? Order { get; set; }
        }
    }
}
